using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Interceptors;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using KrakenDeploy.Server.Data.Spaces;
using KrakenDeploy.Server.Data.Storage;
using KrakenDeploy.Server.Data.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers envelope encryption (M13.D.2): the <see cref="IDekProvider"/>
    /// singleton (holds the KEK, caches the unwrapped DEK) + the
    /// <see cref="IEncryptionService"/> that keys off it. Both the web host and
    /// every CLI command call this with the KEK from <c>Encryption:MasterKey</c>,
    /// so there is a single wiring point. First-boot DEK generation is a separate
    /// explicit step (<see cref="IDekProvider.EnsureDekAsync"/>) run after migrate.
    /// </summary>
    public static IServiceCollection AddKrakenDeployEncryption(
        this IServiceCollection services, string kekBase64)
    {
        services.AddSingleton<IDekProvider>(sp =>
            new DekProvider(sp.GetRequiredService<IServiceScopeFactory>(), kekBase64));
        services.AddSingleton<IEncryptionService, AesEncryptionService>();
        return services;
    }

    public static IServiceCollection AddKrakenDeployData(
        this IServiceCollection services,
        string connectionString,
        string dataPath = "data",
        bool multiAccount = false,
        KrakenDataOptions? dataOptions = null)
    {
        // C3/T1-19 — connection resiliency. Defaults to OFF so CLI callers keep
        // their current bare behaviour (retry is incompatible with the CLI's
        // rotate-dek transaction; see KrakenDataOptions). The web host passes an
        // instance with retry + a pool cap.
        var opts = dataOptions ?? new KrakenDataOptions();
        var effectiveConnectionString = opts.MaxPoolSize is int cap
            ? NpgsqlConnectionStrings.WithMaxPoolSize(connectionString, cap)
            : connectionString;

        services.TryAddTimeProvider();
        // SSRF policy options. Secure defaults (deny loopback/private) apply when the
        // host binds no `Ssrf` config section; the Server host binds the section over
        // these. See SsrfOptions / SsrfGuard.
        services.AddOptions<SsrfOptions>();
        services.AddSingleton<AuditableEntityInterceptor>();
        // AuditLogInterceptor uses IHttpContextAccessor — register it here so
        // AddHttpContextAccessor() is called before AddDbContext (the interceptor
        // needs it at singleton resolution time).
        services.AddHttpContextAccessor();
        services.AddSingleton<AuditLogInterceptor>();
        // Polymorphic tag_applications cleanup — must run BEFORE
        // AuditLogInterceptor (registration order = execution order) so the
        // application deletes it stages are visible to the audit snapshot.
        services.AddSingleton<TagApplicationCleanupInterceptor>();
        // Strips deleted environment ids out of jsonb reference documents
        // (lifecycle phases, freezes, subscriptions). Same rationale as above.
        services.AddSingleton<EnvironmentReferenceCleanupInterceptor>();
        // Deletes RBAC grants whose scope set is emptied by a resource delete,
        // so a per-dimension CASCADE can't silently widen them to whole-Space.
        services.AddSingleton<RoleAssignmentScopeCleanupInterceptor>();

        // ── Space context ─────────────────────────────────────────────────────
        // Default impl always returns the Default Space — used by tests, the
        // migration host, and the create-admin CLI. The Server project replaces
        // this with HttpSpaceContext so requests resolve the Space from routing
        // / claims. Scoped because per-request overrides via WithSpace() must
        // not leak across requests.
        services.TryAddScoped<ISpaceContext, DefaultSpaceContext>();
        services.AddScoped<SpaceScopingInterceptor>();

        // Default account context: multi-account OFF. Ensures the tenant DbContext
        // always has an IAccountContext to construct against (the EF factory resolves
        // ctor params via the container). The Server replaces this with
        // HttpAccountContext when MultiAccount:Enabled is set.
        services.TryAddScoped<IAccountContext, DisabledAccountContext>();

        // Default configuration: data services that read config knobs (WP9
        // RetentionService's Server:DataPath) resolve IConfiguration from the
        // container. The web host + CLI already register the real configuration
        // (this TryAdd never overrides it); this default only kicks in for bare
        // AddKrakenDeployData consumers (tests) and carries the same dataPath the
        // stores were constructed with so file paths stay consistent.
        services.TryAddSingleton<IConfiguration>(_ =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Server:DataPath"] = dataPath,
                })
                .Build());
        // No-op resolver so AccountBoundary can inject IAccountResolver
        // unconditionally; the control plane replaces it with the catalog resolver.
        services.TryAddScoped<IAccountResolver, NullAccountResolver>();

        // No-op OIDC scheme-cache evictor so IdentityProviderService can depend on it
        // unconditionally; the Server replaces it with a real evictor when multi-account
        // is enabled (single-instance OIDC applies edits on restart — nothing to evict).
        services.TryAddScoped<KrakenDeploy.Server.Core.Domain.Security.IOidcSchemeCacheInvalidator,
                              KrakenDeploy.Server.Data.Identity.NullOidcSchemeCacheInvalidator>();

        // In multi-account (SaaS) mode the tenant connection string is per-account:
        // the provider resolves IAccountContext from the scoped `sp` and returns the
        // active account's connection. The Scoped factory lifetime makes this safe —
        // a user stays in one account per circuit (D4), so the per-scope options cache
        // holds the right connection. When no provider is supplied (on-prem / CLI /
        // tests) the fixed connection string is used — behaviour is unchanged.
        services.AddDbContextFactory<KrakenDbContext>((sp, options) =>
        {
            // Single-instance / fallback connection. In multi-account mode the
            // connection is overridden per request in KrakenDbContext.OnConfiguring
            // from the resolved IAccountContext (the factory is Scoped and injects it).
            // NOTE: the MA override re-calls UseNpgsql there without this resiliency
            // config, so retry currently applies to single-instance only. That is
            // fine while MA is fenced off (Program.cs refuses to boot MA), but the
            // OnConfiguring override must mirror this when per-account DEK lands.
            options.UseNpgsql(effectiveConnectionString, npgsql =>
            {
                // C3/T1-19 — retry transient failures (connection drop, Postgres
                // failover) instead of hard-failing the query. Web-host only; see
                // KrakenDataOptions for why the CLI must NOT enable this.
                if (opts.EnableRetryOnFailure)
                {
                    npgsql.EnableRetryOnFailure(
                        opts.MaxRetryCount, opts.MaxRetryDelay, errorCodesToAdd: null);
                }
            });
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<TagApplicationCleanupInterceptor>(),
                sp.GetRequiredService<EnvironmentReferenceCleanupInterceptor>(),
                sp.GetRequiredService<RoleAssignmentScopeCleanupInterceptor>(),
                sp.GetRequiredService<AuditLogInterceptor>(),
                sp.GetRequiredService<SpaceScopingInterceptor>());
        }, ServiceLifetime.Scoped);

        // Package + artifact stores — local filesystem. In multi-account they are SCOPED
        // and namespace their file tree by the active account (resolved from the scope's
        // IAccountContext) so no two tenants share storage; single-instance keeps the flat
        // shared singleton. All consumers are scoped/per-call, so scoping the stores adds
        // no captive dependency.
        if (multiAccount)
        {
            services.AddScoped<IPackageStore>(sp =>
                new LocalPackageStore(dataPath, sp.GetRequiredService<IAccountContext>()));
            services.AddScoped<IArtifactStore>(sp =>
                new LocalArtifactStore(dataPath, sp.GetRequiredService<IAccountContext>()));
        }
        else
        {
            services.AddSingleton<IPackageStore>(_ => new LocalPackageStore(dataPath, new DisabledAccountContext()));
            services.AddSingleton<IArtifactStore>(_ => new LocalArtifactStore(dataPath, new DisabledAccountContext()));
        }
        services.AddScoped<ArtifactService>();

        // In-process deployment dispatch queue.
        // Unbounded: a server restart drops in-flight Queued deployments; they will
        // be re-queued on next startup (handled at startup in a future polish pass).
        services.AddSingleton(Channel.CreateUnbounded<TenantWorkItem>(
            new UnboundedChannelOptions { SingleReader = true }));

        services.AddScoped<SpaceService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<EnvironmentService>();
        services.AddScoped<TargetService>();
        services.AddScoped<TargetRegistrationService>();
        services.AddScoped<PackageService>();
        services.AddScoped<ProcessService>();
        services.AddScoped<ReleaseService>();
        services.AddScoped<DeploymentService>();
        // WP3 — manual-intervention gates. Scoped like every other mutating service;
        // its Channel<TenantWorkItem> dependency is the same singleton the dispatch
        // path already uses, so an approval enqueues onto the one task channel.
        services.AddScoped<InterruptionService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<PivotViewService>();
        services.AddScoped<ProjectDashboardViewService>();
        services.AddScoped<DashboardLayoutService>();
        services.AddScoped<ProjectTransferService>();
        services.AddScoped<VariableService>();
        services.AddScoped<StepTemplateService>();
        services.AddScoped<StepTemplateCatalogService>();
        services.AddScoped<StepPackageCatalogService>();
        services.AddScoped<StepPackageService>();
        services.AddScoped<StepPackageResolver>();
        // M11.A.3 — EF-backed AI audit sink. Replaces the no-op default
        // registered by KrakenDeploy.Ai.AddKrakenAi() so production rows
        // land in the ai_call_logs table.
        services.AddScoped<KrakenDeploy.Ai.IKrakenAiCallSink,
                           Services.Ai.DbKrakenAiCallSink>();
        // M11.A.5 — EF-backed budget tracker. Sums AiCallLog.CostUsd over
        // the current UTC month for the current Space; the wrapper refuses
        // calls when MTD >= BudgetUsdPerMonth.
        services.AddScoped<KrakenDeploy.Ai.IBudgetTracker,
                           Services.Ai.DbBudgetTracker>();
        // M11.A.6.2 — EF-backed settings provider. Reads the Space's
        // SpaceAiSettings row, decrypts the API key, projects to
        // KrakenAiSettings. Replaces the no-op default Ai.AddKrakenAi
        // would have registered (it uses TryAdd, so this Add wins).
        services.AddScoped<KrakenDeploy.Ai.IKrakenAiSettingsProvider,
                           Services.Ai.DbKrakenAiSettingsProvider>();
        // M11.A.6.3 — CRUD service backing the AI-settings REST endpoints.
        // Distinct from DbKrakenAiSettingsProvider: the provider is the
        // read-only path optimised for the LLM-call hot loop (no masking,
        // no audit overhead). This service handles update + masking + reveal.
        services.AddScoped<Services.Ai.SpaceAiSettingsService>();

        // M11.B — AI context builders + step-config curators. The shared
        // kernel consumed by both the MCP server (Tools / Resources) and
        // the M11.C diagnosis job. Curators are singletons (stateless pure
        // functions); the registry + builders sit alongside.
        services.AddStepConfigCurators();
        services.AddScoped<Services.Ai.ContextBuilders.ProcessContextBuilder>();
        services.AddScoped<Services.Ai.ContextBuilders.DeploymentContextBuilder>();
        services.AddScoped<Services.Ai.ContextBuilders.TargetHealthBuilder>();
        services.AddScoped<Services.Ai.ContextBuilders.ReleaseContextBuilder>();
        services.AddScoped<Services.Ai.ContextBuilders.DeploymentDiffBuilder>();

        // M11.C — autonomous failure diagnosis: context assembler + service.
        // The service is best-effort (AI-unavailable never affects deployment
        // status); it's invoked by the DeploymentDiagnosisWorker off the
        // diagnosis channel.
        services.AddScoped<Services.Ai.Diagnosis.DiagnosisContextAssembler>();
        services.AddScoped<Services.Ai.Diagnosis.DeploymentDiagnosisService>();

        // M11.D — process-builder assistant (step suggester, field
        // explanations, script-editor streaming). All under
        // KrakenAiFeature.Assistant.
        services.AddScoped<Services.Ai.Assistant.ProcessAssistantService>();
        services.AddScoped<TenantService>();
        // Extended tag sets (Space-level) — docs/extended-tag-sets-plan.md.
        services.AddScoped<TagService>();
        services.AddScoped<LifecycleService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<RetentionService>();
        // D1 engine merge: RunbookService enqueues onto the shared
        // Channel<TenantWorkItem> (registered above) the unified DeploymentWorker
        // reads — the dedicated RunbookRunChannel is gone.
        services.AddScoped<RunbookService>();
        // IRunbookTrigger surface — narrow interface consumed by the
        // M13.B.2/3 RunbookTransport. RunbookService implements it; the
        // alias keeps the transport's dependency surface small (and the
        // test surface stub-able without instantiating RunbookService).
        services.AddScoped<IRunbookTrigger>(sp => sp.GetRequiredService<RunbookService>());
        services.AddScoped<DropBundleService>();
        services.AddScoped<OfflineResultService>();
        services.AddScoped<BuiltInStepPackageSeeder>();
        services.AddScoped<StepTypeRegistry>();
        services.AddScoped<StepSchemaResolver>();
        services.AddScoped<BuiltInRbacSeeder>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<UserService>();
        services.AddScoped<ApiKeyService>();
        // Pure in-memory throttle gate (no DbContext capture) — safe as a
        // singleton in both modes: it stores only keyId→timestamp, and the
        // actual last-used UPDATE rides the caller's account-routed context.
        services.AddSingleton<ApiKeyUsageTracker>();
        services.AddScoped<TeamService>();
        services.AddScoped<RoleService>();
        services.AddScoped<IdentityProviderService>();
        services.AddScoped<IAuditLog, AuditLogService>();
        services.AddScoped<AuditLogService>(); // also register concrete for PurgeOldEntriesAsync
        services.AddScoped<SmtpSettingsService>();
        services.AddSingleton<KrakenDeploy.Server.Core.Domain.Features.IFeatureCatalog,
                              KrakenDeploy.Server.Core.Domain.Features.BuiltInFeatureCatalog>();

        // SettingsService owns the cache for the unified `settings` documents; the
        // settings-backed services below (feature flags, maintenance, performance)
        // are now thin delegators over it. These open a tenant DbContext via the
        // scope factory. In SaaS multi-account mode the tenant connection is
        // resolved per request from the active account, so a process-wide Singleton
        // (with a single shared cache) can't serve multiple tenants — and, worse,
        // the shared Default-Space id would let one account's document leak to
        // another. Register them Scoped when multi-account is active so each request
        // resolves its own account's DB and the cache is per-request. Single-instance
        // installs keep the shared Singleton cache. DeploymentFreezeService keeps its
        // own cache and rides the same split.
        if (multiAccount)
        {
            services.AddScoped<SettingsService>();
            services.AddScoped<FeatureFlagService>();
            services.AddScoped<DeploymentFreezeService>();
            services.AddScoped<MaintenanceModeService>();
            services.AddScoped<PerformanceSettingsService>();
        }
        else
        {
            services.AddSingleton<SettingsService>();
            services.AddSingleton<FeatureFlagService>();
            services.AddSingleton<DeploymentFreezeService>();
            // Maintenance mode (M13.A.3) — the middleware hits GetStateAsync on
            // every non-exempt write request; SettingsService's short-TTL cache
            // keeps that off the DB.
            services.AddSingleton<MaintenanceModeService>();
            services.AddSingleton<PerformanceSettingsService>();
        }
        // Helper recurring jobs call to short-circuit during maintenance.
        services.AddScoped<MaintenancePause>();
        services.AddScoped<EventSubscriptionService>();
        services.AddScoped<KrakenDeploy.Server.Data.Services.Subscriptions.EventDispatcher>();
        // Subscriptions transports — registered as IEventTransport so the
        // dispatcher can pick the matching implementation by enum. Add a
        // new transport: implement IEventTransport + register here.
        services.AddHttpClient<KrakenDeploy.Server.Data.Services.Subscriptions.WebhookTransport>()
            // SSRF: pin the validated IP per connection and refuse redirects (a
            // webhook receiver returning 3xx is treated as a delivery failure, not
            // followed to a potentially internal target).
            .ConfigurePrimaryHttpMessageHandler(sp =>
                SsrfHttpHandlerFactory.Create(
                    sp.GetRequiredService<IOptions<SsrfOptions>>().Value.Webhook,
                    allowAutoRedirect: false));
        services.AddScoped<
            KrakenDeploy.Server.Data.Services.Subscriptions.IEventTransport,
            KrakenDeploy.Server.Data.Services.Subscriptions.WebhookTransport>(
            sp => sp.GetRequiredService<KrakenDeploy.Server.Data.Services.Subscriptions.WebhookTransport>());
        // Runbook + AI + Email transports (Phase 3).
        services.AddScoped<KrakenDeploy.Server.Data.Services.Subscriptions.RunbookTransport>();
        services.AddScoped<
            KrakenDeploy.Server.Data.Services.Subscriptions.IEventTransport,
            KrakenDeploy.Server.Data.Services.Subscriptions.RunbookTransport>(
            sp => sp.GetRequiredService<KrakenDeploy.Server.Data.Services.Subscriptions.RunbookTransport>());
        services.AddScoped<KrakenDeploy.Server.Data.Services.Subscriptions.AiInspectTransport>();
        services.AddScoped<
            KrakenDeploy.Server.Data.Services.Subscriptions.IEventTransport,
            KrakenDeploy.Server.Data.Services.Subscriptions.AiInspectTransport>(
            sp => sp.GetRequiredService<KrakenDeploy.Server.Data.Services.Subscriptions.AiInspectTransport>());
        services.AddScoped<KrakenDeploy.Server.Data.Services.Subscriptions.EmailImmediateTransport>();
        services.AddScoped<
            KrakenDeploy.Server.Data.Services.Subscriptions.IEventTransport,
            KrakenDeploy.Server.Data.Services.Subscriptions.EmailImmediateTransport>(
            sp => sp.GetRequiredService<KrakenDeploy.Server.Data.Services.Subscriptions.EmailImmediateTransport>());
        // Digest sender — used by EmailDigestFlushJob; shares the MailKit
        // handshake shape with EmailImmediateTransport so "test SMTP passes"
        // implies "digest delivery will work too".
        services.AddScoped<KrakenDeploy.Server.Data.Services.Subscriptions.EmailDigestSender>();
        services.AddTransient<SubscriptionPollerJob>();
        services.AddTransient<EmailDigestFlushJob>();
        // Backup engine + service (M13.G). Scoped because the service opens
        // its own DbContext per call (manual UI invocation + Hangfire schedule
        // both go through it). BackupJob resolves out of the Hangfire scope.
        services.AddScoped<BackupEngine>();
        services.AddScoped<BackupService>();

        // ── Hangfire background jobs ──────────────────────────────────────────
        // Transient so Hangfire's AspNetCoreJobActivator creates a fresh scope
        // per execution (scoped dependencies like KrakenDbContext are resolved
        // within that scope).
        services.AddTransient<AuditRetentionJob>();
        services.AddTransient<AgentLastSeenOfflineJob>();
        services.AddTransient<RegistrationTokenExpiryJob>();
        services.AddTransient<ScheduledDeploymentDispatchJob>();
        services.AddTransient<InterruptionTimeoutJob>();
        services.AddTransient<StepTemplateCatalogPollJob>();
        services.AddTransient<StepPackageCatalogPollJob>();
        services.AddTransient<BackupJob>();
        services.AddTransient<RetentionSweepJob>();

        // Octodiff delta generation — singleton because it has no mutable state;
        // signatures are cached on disk alongside the package files.
        services.AddSingleton<PackageDeltaService>();

        return services;
    }

    public static IdentityBuilder AddKrakenDeployIdentityCore(this IServiceCollection services)
    {
        // No .AddRoles<>() — KrakenDeploy has its own Role/Team/RoleAssignment
        // RBAC model in Server.Core.Domain.Security. Identity is used only for
        // user accounts and password hashing.
        return services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<KrakenDbContext>();
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(s => s.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}

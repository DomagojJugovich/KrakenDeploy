using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Interceptors;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using KrakenDeploy.Server.Data.Spaces;
using KrakenDeploy.Server.Data.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KrakenDeploy.Server.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKrakenDeployData(
        this IServiceCollection services,
        string connectionString,
        string dataPath = "data")
    {
        services.TryAddTimeProvider();
        services.AddSingleton<AuditableEntityInterceptor>();
        // AuditLogInterceptor uses IHttpContextAccessor — register it here so
        // AddHttpContextAccessor() is called before AddDbContext (the interceptor
        // needs it at singleton resolution time).
        services.AddHttpContextAccessor();
        services.AddSingleton<AuditLogInterceptor>();

        // ── Space context ─────────────────────────────────────────────────────
        // Default impl always returns the Default Space — used by tests, the
        // migration host, and the create-admin CLI. The Server project replaces
        // this with HttpSpaceContext so requests resolve the Space from routing
        // / claims. Scoped because per-request overrides via WithSpace() must
        // not leak across requests.
        services.TryAddScoped<ISpaceContext, DefaultSpaceContext>();
        services.AddScoped<SpaceScopingInterceptor>();

        services.AddDbContextFactory<KrakenDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString);
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<AuditLogInterceptor>(),
                sp.GetRequiredService<SpaceScopingInterceptor>());
        }, ServiceLifetime.Scoped);

        // Package store — local filesystem for M2.
        services.AddSingleton<IPackageStore>(_ => new LocalPackageStore(dataPath));

        // Artifact store — local filesystem for M5.5.
        services.AddSingleton<IArtifactStore>(_ => new LocalArtifactStore(dataPath));
        services.AddScoped<ArtifactService>();

        // In-process deployment dispatch queue.
        // Unbounded: a server restart drops in-flight Queued deployments; they will
        // be re-queued on next startup (handled at startup in a future polish pass).
        services.AddSingleton(Channel.CreateUnbounded<Guid>(
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
        services.AddScoped<TenantService>();
        services.AddScoped<LifecycleService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<RetentionService>();
        services.AddSingleton<RunbookRunChannel>();
        services.AddScoped<RunbookService>();
        // IRunbookTrigger surface — narrow interface consumed by the
        // M13.B.2/3 RunbookTransport. RunbookService implements it; the
        // alias keeps the transport's dependency surface small (and the
        // test surface stub-able without instantiating RunbookService).
        services.AddScoped<IRunbookTrigger>(sp => sp.GetRequiredService<RunbookService>());
        services.AddScoped<DropBundleService>();
        services.AddScoped<OfflineResultService>();
        services.AddScoped<BuiltInStepTemplateSeeder>();
        services.AddScoped<BuiltInStepPackageSeeder>();
        services.AddScoped<BuiltInRbacSeeder>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<UserService>();
        services.AddScoped<TeamService>();
        services.AddScoped<RoleService>();
        services.AddScoped<IdentityProviderService>();
        services.AddScoped<IAuditLog, AuditLogService>();
        services.AddScoped<AuditLogService>(); // also register concrete for PurgeOldEntriesAsync
        services.AddScoped<SmtpSettingsService>();
        services.AddSingleton<KrakenDeploy.Server.Core.Domain.Features.IFeatureCatalog,
                              KrakenDeploy.Server.Core.Domain.Features.BuiltInFeatureCatalog>();
        // Singleton because the cache must persist across requests — the
        // service opens its own DbContext per call via the factory.
        services.AddSingleton<FeatureFlagService>();
        services.AddSingleton<DeploymentFreezeService>();
        // Maintenance mode (M13.A.3) — singleton so the cached state is
        // shared across requests; the middleware hits GetStateAsync on
        // every non-exempt write request.
        services.AddSingleton<MaintenanceModeService>();
        // Performance + retention knobs (M13.F.3) — singleton so the
        // 30 s cache is shared across consumers (Hangfire jobs, the
        // DeploymentWorker, the page itself).
        services.AddSingleton<PerformanceSettingsService>();
        // Helper recurring jobs call to short-circuit during maintenance.
        services.AddScoped<MaintenancePause>();
        services.AddScoped<EventSubscriptionService>();
        services.AddScoped<KrakenDeploy.Server.Data.Services.Subscriptions.EventDispatcher>();
        // Subscriptions transports — registered as IEventTransport so the
        // dispatcher can pick the matching implementation by enum. Add a
        // new transport: implement IEventTransport + register here.
        services.AddHttpClient<KrakenDeploy.Server.Data.Services.Subscriptions.WebhookTransport>();
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
        services.AddTransient<StepTemplateCatalogPollJob>();
        services.AddTransient<StepPackageCatalogPollJob>();
        services.AddTransient<BackupJob>();

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

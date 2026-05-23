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
        services.AddScoped<TenantService>();
        services.AddScoped<LifecycleService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<RetentionService>();
        services.AddSingleton<RunbookRunChannel>();
        services.AddScoped<RunbookService>();
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

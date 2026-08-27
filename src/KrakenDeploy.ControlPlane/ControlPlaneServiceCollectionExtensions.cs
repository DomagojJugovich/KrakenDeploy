using KrakenDeploy.ControlPlane.Accounts;
using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.ControlPlane.Provisioning;
using KrakenDeploy.ControlPlane.Secrets;
using KrakenDeploy.Platform;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KrakenDeploy.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SaaS control plane: the catalog DbContext factory, the
    /// subdomain → account resolver (cached), the secret store, and the provisioning
    /// + fleet-migration services. Call only when <c>MultiAccount:Enabled</c> is set;
    /// the request pipeline (account context + middleware) is wired separately in the
    /// Server host.
    /// </summary>
    public static IServiceCollection AddKrakenControlPlane(
        this IServiceCollection services,
        IConfiguration configuration,
        string catalogConnectionString,
        string dataPath = "data")
    {
        services.AddOptions<MultiAccountOptions>()
            .Bind(configuration.GetSection(MultiAccountOptions.SectionName));

        services.AddMemoryCache();

        // Control-plane catalog DB — its own database, fixed connection (Singleton
        // factory is correct: routing metadata, no per-request connection variance).
        services.AddDbContextFactory<CatalogDbContext>(options =>
        {
            options.UseNpgsql(catalogConnectionString);
            options.UseSnakeCaseNamingConvention();
        });

        // Secret store is stateful (owns a file + lock) → singleton.
        services.AddSingleton<ISecretStore>(_ => new FileSecretStore(dataPath));

        services.AddScoped<IAccountResolver, CatalogAccountResolver>();
        services.AddScoped<ICatalogStore, CatalogStore>();
        services.AddScoped<IDatabaseProvisioner, PostgresDatabaseProvisioner>();
        services.AddScoped<IDnsProvisioner, NoopDnsProvisioner>();
        services.AddScoped<TenantInitializer>();
        services.AddScoped<IAccountProvisioner, AccountProvisioner>();
        services.AddScoped<FleetMigrationOrchestrator>();
        // Transient so Hangfire's activator creates it per execution; it opens its
        // own per-account scopes via IServiceScopeFactory.
        services.AddTransient<PerAccountRecurringJobRunner>();

        // Blue-green release registry (register/flip/retire + drain watcher). Under
        // Saas the two tables physically stay in the catalog (public schema, owned
        // by the catalog migration chain) — the PlatformReleaseDbContext just maps
        // them there (BG1/T3). TimeProvider via TryAdd inside so a host that
        // already registered one (or a test fake) wins.
        services.AddPlatformReleaseRegistry(catalogConnectionString, ownSchema: false);

        return services;
    }
}

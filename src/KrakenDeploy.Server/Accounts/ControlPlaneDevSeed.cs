using KrakenDeploy.ControlPlane.Catalog;
using KrakenDeploy.ControlPlane.Provisioning;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Accounts;

/// <summary>
/// Development-only seed for the multi-account control plane: migrates the catalog
/// database, ensures a local dev shard, and provisions a couple of demo accounts so
/// the app serves them by subdomain (e.g. <c>acme.localhost</c>). Idempotent — safe
/// to run on every dev startup.
/// </summary>
internal static class ControlPlaneDevSeed
{
    private static readonly (string Subdomain, string Name)[] DemoAccounts =
    [
        ("acme", "Acme Corp"),
        ("globex", "Globex"),
    ];

    public static async Task SeedAsync(
        IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // 1. Migrate the control-plane catalog database.
        var catalogFactory = sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>();
        await using (var catalog = await catalogFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await catalog.Database.MigrateAsync().ConfigureAwait(false);
        }

        // 2. Ensure a dev shard whose admin secret points at the local Postgres
        //    server (the role needs CREATEDB). Override via ConnectionStrings:ShardAdmin.
        var adminConn = configuration.GetConnectionString("ShardAdmin")
            ?? configuration.GetConnectionString("KrakenDb")
            ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres";
        const string shardSecretRef = "shard/dev-local";

        var secrets = sp.GetRequiredService<ISecretStore>();
        await secrets.StoreAsync(shardSecretRef, adminConn).ConfigureAwait(false);

        await using (var catalog = await catalogFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            if (!await catalog.Shards.AnyAsync().ConfigureAwait(false))
            {
                catalog.Shards.Add(new Shard
                {
                    Name = "dev-local",
                    HostSecretRef = shardSecretRef,
                    Capacity = 100,
                    Status = ShardStatus.Online,
                });
                await catalog.SaveChangesAsync().ConfigureAwait(false);
                logger.LogInformation("Seeded dev shard 'dev-local'.");
            }
        }

        // 3. Provision the demo accounts (idempotent — skip if already present).
        var store = sp.GetRequiredService<ICatalogStore>();
        var provisioner = sp.GetRequiredService<IAccountProvisioner>();

        foreach (var (subdomain, name) in DemoAccounts)
        {
            if (await store.GetBySubdomainAsync(subdomain).ConfigureAwait(false) is not null)
            {
                logger.LogInformation("Demo account '{Subdomain}' already provisioned.", subdomain);
                continue;
            }

            var result = await provisioner.ProvisionAsync(
                    new NewAccountRequest(subdomain, name, $"admin@{subdomain}.local", "ChangeMe!123456"))
                .ConfigureAwait(false);

            if (result.Success)
            {
                logger.LogInformation(
                    "Provisioned demo account '{Subdomain}' (admin: admin@{Subdomain}.local).", subdomain, subdomain);
            }
            else
            {
                logger.LogError(
                    "Failed to provision demo account '{Subdomain}': {Error}", subdomain, result.Error);
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.ControlPlane.Catalog;

/// <summary>
/// The control-plane catalog: maps subdomain → account → tenant DB connection
/// (by secret reference) plus shard placement. Small, central, heavily cached;
/// routing metadata only, no customer PII. Lives in its OWN database, separate
/// from every tenant database. See <c>docs/saas-multi-account-architecture.md</c> §8.
/// </summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<BusinessAccount> BusinessAccounts => Set<BusinessAccount>();

    public DbSet<Shard> Shards => Set<Shard>();

    // NOTE (BG1/T3): app_releases + platform_settings were created by this
    // context's AddReleaseRegistry migration and PHYSICALLY stay in the catalog
    // under Saas, but their model/ownership moved to PlatformReleaseDbContext
    // (KrakenDeploy.Platform) so the registry also works on-prem. The
    // TransferReleaseRegistryToPlatform migration is deliberately empty — no
    // data move, the tables just left this model.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}

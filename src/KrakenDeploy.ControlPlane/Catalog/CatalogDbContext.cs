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

    /// <summary>Blue-green release registry (docs/blue-green-slot-deployment.md §4).</summary>
    public DbSet<AppRelease> AppReleases => Set<AppRelease>();

    /// <summary>Platform-global settings, e.g. the current default release pointer.</summary>
    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}

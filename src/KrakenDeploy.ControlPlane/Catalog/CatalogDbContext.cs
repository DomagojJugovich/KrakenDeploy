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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}

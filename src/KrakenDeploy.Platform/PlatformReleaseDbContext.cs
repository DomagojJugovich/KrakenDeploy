using KrakenDeploy.Platform.Releases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace KrakenDeploy.Platform;

/// <summary>
/// Minimal context for the blue-green release registry — exactly two tables
/// (<c>app_releases</c> + <c>platform_settings</c>) plus the advisory-lock
/// transitions in <see cref="Releases.ReleaseRegistry"/> (BG1/T3).
/// <para>
/// Where the tables live depends on <c>Deployment:Topology</c>:
/// <list type="bullet">
/// <item><b>Saas</b> — the catalog database, <c>public</c> schema
/// (<see cref="PlatformReleaseSchema.Schema"/> null): the same physical tables the
/// catalog migrations created — no data move, and this context never migrates
/// them (the catalog migration chain keeps ownership of DDL there).</item>
/// <item><b>OnPremBlueGreen</b> — KrakenDb, dedicated <c>platform</c> schema with
/// its OWN migrations-history table (<c>platform.__EFMigrationsHistory_platform</c>)
/// so WP-BASELINE's squash of the app schema stays untouched.</item>
/// <item><b>OnPrem</b> — not registered at all.</item>
/// </list>
/// The per-node router deliberately does NOT use this context — it reads the two
/// tables with raw Npgsql (in OnPremBlueGreen its connection string carries
/// <c>Search Path=platform</c> so the unqualified table names resolve).
/// </para>
/// </summary>
public class PlatformReleaseDbContext(
    DbContextOptions<PlatformReleaseDbContext> options,
    PlatformReleaseSchema schema)
    : DbContext(options)
{
    /// <summary>Schema the two tables map to (null = provider default / public).</summary>
    internal string? Schema { get; } = schema.Schema;

    public DbSet<AppRelease> AppReleases => Set<AppRelease>();

    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();

    /// <summary>
    /// The ONE OnPremBlueGreen options recipe: Npgsql with the dedicated
    /// migrations-history table, snake_case naming, and the schema-aware model
    /// cache key. Every constructor of this context under that topology (DI,
    /// CLI commands, design time, tests) goes through here so the pieces can
    /// never drift — pair it with
    /// <c>new PlatformReleaseSchema(PlatformReleaseSchema.OnPremSchemaName)</c>.
    /// </summary>
    public static DbContextOptions<PlatformReleaseDbContext> CreateOnPremOptions(
        string connectionString)
    {
        var builder = new DbContextOptionsBuilder<PlatformReleaseDbContext>();
        ConfigureOptions(builder, connectionString, ownSchema: true);
        return builder.Options;
    }

    /// <summary>
    /// Options recipe shared by both blue-green topologies.
    /// <paramref name="ownSchema"/> true → OnPremBlueGreen (dedicated
    /// <c>platform</c> schema + own history table); false → Saas (catalog
    /// <c>public</c> schema — the catalog migration chain owns DDL, so no
    /// history table here). Non-generic builder overload so
    /// <c>AddDbContextFactory</c> callbacks can use it too.
    /// </summary>
    public static void ConfigureOptions(
        DbContextOptionsBuilder optionsBuilder, string connectionString, bool ownSchema)
    {
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            if (ownSchema)
            {
                npgsql.MigrationsHistoryTable(
                    PlatformReleaseSchema.MigrationsHistoryTableName,
                    PlatformReleaseSchema.OnPremSchemaName);
            }
        });
        optionsBuilder.UseSnakeCaseNamingConvention();
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, PlatformReleaseModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppRelease>(builder =>
        {
            builder.ToTable("app_releases", Schema);
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Id).HasMaxLength(100);
            builder.Property(x => x.Label).HasMaxLength(200).IsRequired();

            builder.Property(x => x.Status).IsRequired().HasConversion<int>();
            builder.HasIndex(x => x.Status);

            builder.Property(x => x.DeployedAtUtc).IsRequired();

            // Invariant (runbook step 0): a slot hosts at most ONE non-Retired release.
            // Enforced at the DB so a mis-sequenced register can never corrupt routing.
            // (3 = AppReleaseStatus.Retired, int-converted.)
            builder.HasIndex(x => x.SlotNo)
                .IsUnique()
                .HasFilter("status <> 3")
                .HasDatabaseName("ux_app_releases_slot_no_live");
        });

        modelBuilder.Entity<PlatformSetting>(builder =>
        {
            builder.ToTable("platform_settings", Schema);
            builder.HasKey(x => x.Key);

            builder.Property(x => x.Key).HasMaxLength(100);
            builder.Property(x => x.Value).HasMaxLength(2000).IsRequired();
            builder.Property(x => x.ModifiedUtc).IsRequired();
        });
    }
}

/// <summary>
/// Schema the <see cref="PlatformReleaseDbContext"/> maps its tables to — a DI
/// singleton so <c>IDbContextFactory</c> can construct the context. One process
/// only ever uses one value, but tests may host both shapes side by side, so the
/// model cache key includes it (<see cref="PlatformReleaseModelCacheKeyFactory"/>).
/// </summary>
public sealed record PlatformReleaseSchema(string? Schema)
{
    /// <summary>Dedicated schema used under <c>OnPremBlueGreen</c> (T3).</summary>
    public const string OnPremSchemaName = "platform";

    /// <summary>History table name used when this context owns its DDL (OnPremBlueGreen).</summary>
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory_platform";
}

/// <summary>
/// Keys the cached EF model on the mapped schema as well as the context type —
/// without this, a process that builds the context with two different schemas
/// (test suites do) would silently reuse the first model.
/// </summary>
public sealed class PlatformReleaseModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), (context as PlatformReleaseDbContext)?.Schema, designTime);
}

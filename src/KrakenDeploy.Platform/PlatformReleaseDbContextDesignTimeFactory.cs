using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KrakenDeploy.Platform;

/// <summary>
/// Used by <c>dotnet ef</c> to construct a <see cref="PlatformReleaseDbContext"/>
/// without running the full server. Migrations are generated against the
/// OnPremBlueGreen shape — dedicated <c>platform</c> schema + its own
/// migrations-history table — because that is the only topology in which this
/// context owns DDL (under Saas the catalog migration chain owns the tables).
/// Connection string comes from <c>KRAKEN_PLATFORM_DESIGN_TIME_CONNECTION_STRING</c>
/// when set, otherwise the local docker-compose Postgres.
/// </summary>
public class PlatformReleaseDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<PlatformReleaseDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=krakendeploy_platform_design;Username=postgres;Password=postgres";

    public PlatformReleaseDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("KRAKEN_PLATFORM_DESIGN_TIME_CONNECTION_STRING")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<PlatformReleaseDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                PlatformReleaseSchema.MigrationsHistoryTableName,
                PlatformReleaseSchema.OnPremSchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new PlatformReleaseDbContext(
            options, new PlatformReleaseSchema(PlatformReleaseSchema.OnPremSchemaName));
    }
}

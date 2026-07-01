using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KrakenDeploy.ControlPlane.Catalog;

/// <summary>
/// Used by <c>dotnet ef</c> to construct a <see cref="CatalogDbContext"/> against
/// the control-plane catalog database without running the full server. Connection
/// string comes from <c>KRAKEN_CATALOG_DESIGN_TIME_CONNECTION_STRING</c> when set,
/// otherwise the local docker-compose Postgres.
/// </summary>
public class CatalogDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=krakendeploy_catalog_design;Username=postgres;Password=postgres";

    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("KRAKEN_CATALOG_DESIGN_TIME_CONNECTION_STRING")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CatalogDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// Used by <c>dotnet ef migrations</c> to construct a <see cref="KrakenDbContext"/>
/// without running the full server. Connection string is taken from the
/// <c>KRAKEN_DESIGN_TIME_CONNECTION_STRING</c> environment variable when set,
/// otherwise it falls back to the local docker-compose Postgres.
/// </summary>
public class KrakenDbContextDesignTimeFactory : IDesignTimeDbContextFactory<KrakenDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=krakendeploy_design;Username=postgres;Password=postgres";

    public KrakenDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("KRAKEN_DESIGN_TIME_CONNECTION_STRING")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new KrakenDbContext(options);
    }
}

using KrakenDeploy.Platform.Releases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KrakenDeploy.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the blue-green release registry (<see cref="PlatformReleaseDbContext"/>,
    /// <see cref="ReleaseRegistry"/>, <see cref="ReleaseDrainWatcher"/>,
    /// <see cref="SlotDrainGuard"/>). Called under both blue-green topologies (BG1/T3):
    /// <list type="bullet">
    /// <item><c>Saas</c> — <paramref name="connectionString"/> is the catalog,
    /// <paramref name="ownSchema"/> false (the tables live in <c>public</c>, owned by
    /// the catalog migration chain).</item>
    /// <item><c>OnPremBlueGreen</c> — <paramref name="connectionString"/> is KrakenDb,
    /// <paramref name="ownSchema"/> true (dedicated <c>platform</c> schema with its own
    /// migrations-history table; apply via <c>database setup</c>/<c>upgrade</c>).</item>
    /// </list>
    /// Not called at all under <c>OnPrem</c>.
    /// </summary>
    public static IServiceCollection AddPlatformReleaseRegistry(
        this IServiceCollection services,
        string connectionString,
        bool ownSchema)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new PlatformReleaseSchema(
            ownSchema ? PlatformReleaseSchema.OnPremSchemaName : null));

        // Fixed connection for the whole process lifetime → singleton factory is
        // correct (routing metadata only, no per-request connection variance).
        services.AddDbContextFactory<PlatformReleaseDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                if (ownSchema)
                {
                    npgsql.MigrationsHistoryTable(
                        PlatformReleaseSchema.MigrationsHistoryTableName,
                        PlatformReleaseSchema.OnPremSchemaName);
                }
            });
            options.UseSnakeCaseNamingConvention();
            options.ReplaceService<
                Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory,
                PlatformReleaseModelCacheKeyFactory>();
        });

        services.AddScoped<ReleaseRegistry>();

        // Drain-watcher (kraken.release-drain-watch): transient for Hangfire's
        // activator; short-timeout named client for the /slot-metrics probes.
        services.AddTransient<ReleaseDrainWatcher>();
        // Own-release drain check (singleton: holds a 15s cache over the registry).
        services.AddSingleton<SlotDrainGuard>();
        services.AddSingleton<ISlotDrainGuard>(sp => sp.GetRequiredService<SlotDrainGuard>());
        services.AddHttpClient(ReleaseDrainWatcher.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        return services;
    }
}

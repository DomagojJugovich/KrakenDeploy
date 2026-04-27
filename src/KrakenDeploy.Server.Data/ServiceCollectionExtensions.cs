using KrakenDeploy.Server.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKrakenDeployData(
        this IServiceCollection services,
        string connectionString)
    {
        services.TryAddTimeProvider();
        services.AddSingleton<AuditableEntityInterceptor>();

        services.AddDbContext<KrakenDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString);
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>());
        });

        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(s => s.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}

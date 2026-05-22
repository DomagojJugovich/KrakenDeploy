using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Ai;

/// <summary>
/// DI helpers for the M11.A shared AI infrastructure. Hosts (the server)
/// call <see cref="AddKrakenAi"/> in <c>Program.cs</c>; tests call the
/// same method against a service collection wired with stub providers.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IKrakenAi"/>, <see cref="KrakenAiClientFactory"/>,
    /// and the dependencies the implementation needs.
    /// <para>
    /// Callers MUST separately register an <see cref="IKrakenAiSettingsProvider"/>
    /// — typically a Space-scoped service that reads from the DB. The
    /// factory throws cleanly when no provider is registered, so the
    /// service collection isn't silently broken at startup.
    /// </para>
    /// </summary>
    public static IServiceCollection AddKrakenAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Stateless; safe as a singleton.
        services.AddSingleton<KrakenAiClientFactory>();

        // Per-request — settings are Space-scoped, so the wrapper resolves
        // a fresh settings instance on every call rather than caching.
        services.AddScoped<IKrakenAi, KrakenAi>();

        return services;
    }
}

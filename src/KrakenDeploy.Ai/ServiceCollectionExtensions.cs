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

        // M11.A.4 — prompt sanitiser is pure string replacement, no
        // mutable state, safe as a singleton. Callers thread the per-call
        // sensitive-values map through KrakenAiRequestOptions.
        Microsoft.Extensions.DependencyInjection.Extensions
            .ServiceCollectionDescriptorExtensions
            .TryAddSingleton<IPromptSanitizer, PromptSanitizer>(services);

        // M11.A.5 — cost catalog is a static rate table; safe as a singleton.
        // Operators can override per-installation (custom EA pricing) by
        // registering a custom IAiCostCatalog BEFORE AddKrakenAi.
        Microsoft.Extensions.DependencyInjection.Extensions
            .ServiceCollectionDescriptorExtensions
            .TryAddSingleton<IAiCostCatalog, AiCostCatalog>(services);

        // M11.A.5 — budget tracker default is a no-op (zero MTD). The host
        // (KrakenDeploy.Server.Data) replaces with DbBudgetTracker which
        // sums AiCallLog.CostUsd for the current Space + month.
        Microsoft.Extensions.DependencyInjection.Extensions
            .ServiceCollectionDescriptorExtensions
            .TryAddScoped<IBudgetTracker, NullBudgetTracker>(services);

        // Per-request — settings are Space-scoped, so the wrapper resolves
        // a fresh settings instance on every call rather than caching.
        services.AddScoped<IKrakenAi, KrakenAi>();

        // Default sink is a no-op; the host (KrakenDeploy.Server.Data)
        // overrides via TryAddScoped<IKrakenAiCallSink, DbKrakenAiCallSink>()
        // BEFORE this method runs, or simply replaces this registration
        // after. The TryAddScoped here uses the framework's "if not
        // already registered" semantics so a prior host-side registration
        // wins.
        Microsoft.Extensions.DependencyInjection.Extensions
            .ServiceCollectionDescriptorExtensions
            .TryAddScoped<IKrakenAiCallSink, NullKrakenAiCallSink>(services);

        return services;
    }
}

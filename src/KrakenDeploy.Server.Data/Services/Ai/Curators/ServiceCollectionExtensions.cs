using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Services.Ai.Curators;

/// <summary>
/// DI registration for the step-config curators + the registry that resolves
/// them. Call from the main data-layer registration so the
/// <see cref="StepConfigCuratorRegistry"/> + <c>ProcessContextBuilder</c>
/// resolve cleanly.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in curators (each as <c>IStepConfigCurator</c>),
    /// the <see cref="DefaultStepConfigCurator"/> fallback, and the
    /// <see cref="StepConfigCuratorRegistry"/>. Step packages that ship their
    /// own curator add a single <c>services.AddSingleton&lt;IStepConfigCurator,
    /// MyCurator&gt;()</c> line and it's discovered automatically.
    /// </summary>
    public static IServiceCollection AddStepConfigCurators(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IStepConfigCurator, ScriptStepConfigCurator>();
        services.AddSingleton<IStepConfigCurator, PackageStepConfigCurator>();
        services.AddSingleton<IStepConfigCurator, IisStepConfigCurator>();
        services.AddSingleton<IStepConfigCurator, WindowsServiceStepConfigCurator>();
        services.AddSingleton<IStepConfigCurator, ManualStepConfigCurator>();
        services.AddSingleton<IStepConfigCurator, SubstituteVariablesStepConfigCurator>();
        services.AddSingleton<IStepConfigCurator, StepGroupConfigCurator>();

        services.AddSingleton<DefaultStepConfigCurator>();
        services.AddSingleton<StepConfigCuratorRegistry>();

        return services;
    }
}

using System.Reflection;

namespace KrakenDeploy.Server.Data.Services.Ai.Curators;

/// <summary>
/// Resolves the right <see cref="IStepConfigCurator"/> for a step type and
/// applies it. Built from the DI-registered curators (each read for its
/// <see cref="CuratesStepTypeAttribute"/>s) plus a fallback for step types
/// no curator claims.
/// <para>
/// Registered as a singleton via
/// <see cref="ServiceCollectionExtensions.AddStepConfigCurators"/>; consumes
/// the full set of <c>IStepConfigCurator</c> registrations so a step
/// package adding its own curator is picked up automatically.
/// </para>
/// </summary>
public sealed class StepConfigCuratorRegistry
{
    private readonly Dictionary<string, IStepConfigCurator> _byStepType;
    private readonly IStepConfigCurator _fallback;

    public StepConfigCuratorRegistry(
        IEnumerable<IStepConfigCurator> curators,
        DefaultStepConfigCurator fallback)
    {
        ArgumentNullException.ThrowIfNull(curators);
        ArgumentNullException.ThrowIfNull(fallback);

        _fallback = fallback;
        _byStepType = new Dictionary<string, IStepConfigCurator>(StringComparer.OrdinalIgnoreCase);
        foreach (var curator in curators)
        {
            foreach (var attr in curator.GetType()
                         .GetCustomAttributes<CuratesStepTypeAttribute>(inherit: false))
            {
                // Last registration wins for a given step type — lets a
                // step package override a built-in curator if it ships one
                // with the same [CuratesStepType]. Rare, but the override
                // path is intentional, not a silent collision.
                _byStepType[attr.StepType] = curator;
            }
        }
    }

    /// <summary>
    /// Curates <paramref name="config"/> using the curator registered for
    /// <paramref name="stepType"/>, or the
    /// <see cref="DefaultStepConfigCurator"/> when none claims it. Never
    /// throws on an unknown step type — the fallback always produces
    /// something.
    /// </summary>
    public IReadOnlyDictionary<string, string> Curate(
        string stepType, IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var curator = !string.IsNullOrEmpty(stepType)
                      && _byStepType.TryGetValue(stepType, out var c)
            ? c
            : _fallback;
        return curator.Curate(config);
    }
}

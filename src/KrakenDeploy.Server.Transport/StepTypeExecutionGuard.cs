using KrakenDeploy.Contracts;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// SC4-b pre-dispatch guard: refuses a plan whose step types cannot actually
/// execute, with an actionable reason — instead of the pre-consolidation
/// behavior where the agent died with "Unknown step type" (unserved type) or
/// <c>ServerScriptStepRunner</c> failed with a misleading "Step has no script
/// body" (RunOnServer on a type that doesn't support it).
/// <para>
/// Pure function over per-type facts so it unit-tests without a database;
/// the orchestrator materialises <see cref="TypeFacts"/> from the step-type
/// registry + the serving package's schema.
/// </para>
/// </summary>
public static class StepTypeExecutionGuard
{
    /// <summary>Execution-relevant facts about one step type.</summary>
    /// <param name="Exists">A registry row exists (a package serves it, or it is a System type).</param>
    /// <param name="ServerSide">The registry says the type executes server-side (locus is not AgentPackage).</param>
    /// <param name="SupportsRunOnServer">The serving schema exposes the RunOnServer field — the only types for which the per-step flag is meaningful.</param>
    public sealed record TypeFacts(bool Exists, bool ServerSide, bool SupportsRunOnServer);

    /// <summary>
    /// Returns the refusal reason for the first violating step, or <c>null</c>
    /// when every step can execute. <paramref name="factsByType"/> is invoked
    /// once per distinct step type (case-insensitive).
    /// </summary>
    public static string? FindViolation(
        IReadOnlyList<DeploymentStepPlan> steps,
        Func<string, TypeFacts> factsByType)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(factsByType);

        var cache = new Dictionary<string, TypeFacts>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (!cache.TryGetValue(step.StepType, out var facts))
            {
                cache[step.StepType] = facts = factsByType(step.StepType);
            }

            if (!facts.Exists)
            {
                return $"Step '{step.Name}' has step type '{step.StepType}', which no " +
                       "installed step package serves. Install the package that provides " +
                       "this type (or fix the step's type) and re-run.";
            }

            if (step.RunOnServer && !facts.ServerSide && !facts.SupportsRunOnServer)
            {
                return $"Step '{step.Name}' is marked to run on the server, but step type " +
                       $"'{step.StepType}' does not support server-side execution. Clear " +
                       "the step's run-on-server flag and re-run.";
            }
        }

        return null;
    }
}

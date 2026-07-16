namespace KrakenDeploy.Contracts;

/// <summary>
/// M15 follow-up (moved to Contracts in B4 — the key format IS the
/// operator-facing contract, and the server's online sub-plan merge now
/// shares it) — output-variable accumulation logic extracted from
/// <see cref="DeploymentExecutor"/> so the cross-iteration reference
/// contract (synthetic accumulator key → Octostache resolution) can be
/// unit-tested without spinning up the full executor.
///
/// <para>
/// Subsequent steps see prior steps' outputs as
/// <c>Octopus.Action[StepKey].Output.X</c> entries in
/// <see cref="DeploymentPlan.Variables"/>. <strong>StepKey</strong> is
/// the step's <see cref="DeploymentStepPlan.AccumulatorKey"/> when set
/// (M15.2 ForEach iterations use a stable synthetic key like
/// <c>"Deploy[0]"</c>), otherwise the display
/// <see cref="DeploymentStepPlan.Name"/>. Octostache references like
/// <c>#{Octopus.Action[Deploy[0]].Output.Foo}</c> resolve through that
/// key shape.
/// </para>
/// </summary>
public static class OutputVariableAccumulator
{
    /// <summary>
    /// Returns a copy of <paramref name="basePlan"/> with every
    /// <c>Octopus.Action[StepKey].Output.X</c> entry from
    /// <paramref name="outputsByStep"/> merged into
    /// <see cref="DeploymentPlan.Variables"/>. The caller indexes
    /// <paramref name="outputsByStep"/> by the step's accumulator key
    /// (<see cref="DeploymentStepPlan.AccumulatorKey"/> for iterations,
    /// otherwise <see cref="DeploymentStepPlan.Name"/>) so cross-
    /// iteration references resolve to the right iteration's output.
    /// Returns <paramref name="basePlan"/> unchanged when there are no
    /// prior outputs (common case for the first step).
    /// </summary>
    public static DeploymentPlan AugmentPlanWithPriorOutputs(
        DeploymentPlan basePlan,
        IReadOnlyDictionary<string, Dictionary<string, string>> outputsByStep)
    {
        ArgumentNullException.ThrowIfNull(basePlan);
        ArgumentNullException.ThrowIfNull(outputsByStep);

        if (outputsByStep.Count == 0)
        {
            return basePlan;
        }

        var merged = new Dictionary<string, string>(
            basePlan.Variables, StringComparer.OrdinalIgnoreCase);
        foreach (var (stepKey, outputs) in outputsByStep)
        {
            foreach (var (name, value) in outputs)
            {
                merged[$"Octopus.Action[{stepKey}].Output.{name}"] = value;
            }
        }

        return basePlan with { Variables = merged };
    }
}

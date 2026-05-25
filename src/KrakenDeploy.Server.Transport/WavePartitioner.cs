using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M14.4 — partitions a flat <see cref="DeploymentStepPlan"/> sequence into
/// waves driven by each step's <see cref="StepSnapshot.StartTrigger"/>. A
/// wave is the first step plus all subsequent steps marked
/// <see cref="StepStartTrigger.StartWithPrevious"/>, until a
/// <see cref="StepStartTrigger.StartAfterPrevious"/> step starts wave N+1.
///
/// <para>
/// Replaces the M14.0..3 <c>PartitionIntoGroups</c> (binary same-side
/// consecutive runs) — under M14.4 the orchestrator dispatches one wave at
/// a time, with steps inside the wave running in parallel.
/// </para>
///
/// <para>
/// Mixed-side waves (a wave that mixes server-side and target-side steps)
/// are refused upfront with <see cref="InvalidWaveException"/>. The
/// orchestrator catches the exception, emits a
/// <c>Deployment.MixedWaveRefused</c> audit, and fails the deployment with
/// the carried <see cref="InvalidWaveException.Wave"/> detail — operators
/// see exactly which step names mixed sides.
/// </para>
/// </summary>
public static class WavePartitioner
{
    /// <summary>
    /// Classification of a wave by execution side. Server / Target only —
    /// mixed waves are rejected before construction.
    /// </summary>
    public enum WaveKind
    {
        /// <summary>Every step in the wave runs server-side (Octopus.Action.RunOnServer
        /// or an intrinsically server-only StepType like Octopus.DeployRelease).</summary>
        Server,

        /// <summary>Every step in the wave runs target-side (dispatched to the agent).</summary>
        Target,
    }

    /// <summary>
    /// One wave of steps. The wave's <see cref="Kind"/> dictates whether the
    /// orchestrator runs the steps in-process (Server) or dispatches them as
    /// a single sub-plan to the agent (Target).
    ///
    /// <para>
    /// <see cref="Steps"/> are in their original declared order. The wave's
    /// "first" step (the one that opened the wave with StartAfterPrevious or
    /// the very first step in the process) is <c>Steps[0]</c>.
    /// </para>
    /// </summary>
    public sealed record Wave(WaveKind Kind, IReadOnlyList<DeploymentStepPlan> Steps);

    /// <summary>
    /// Thrown when a wave under construction mixes server-side and target-side
    /// steps. M14.4 v1 refuses this — split into two single-side waves run
    /// sequentially. The <see cref="Wave"/> field carries the offending
    /// wave's step plans, both server-side and target-side step names so the
    /// orchestrator audit can identify which steps were involved.
    /// </summary>
    public sealed class InvalidWaveException(
        string message,
        IReadOnlyList<DeploymentStepPlan> waveSteps,
        IReadOnlyList<string> serverStepNames,
        IReadOnlyList<string> targetStepNames)
        : Exception(message)
    {
        public IReadOnlyList<DeploymentStepPlan> WaveSteps { get; } = waveSteps;
        public IReadOnlyList<string> ServerStepNames { get; } = serverStepNames;
        public IReadOnlyList<string> TargetStepNames { get; } = targetStepNames;
    }

    /// <summary>
    /// The per-step trigger lookup is keyed by <see cref="DeploymentStepPlan.Index"/>
    /// rather than carried on the plan itself because the contract record
    /// holds an <c>int StartTrigger = 0</c> appended for back-compat (see
    /// <see cref="DeploymentStepPlan.StartTrigger"/>). The orchestrator
    /// owns the snapshot rows and feeds the trigger lookup so this helper
    /// doesn't have to know about <see cref="StepSnapshot"/>.
    /// </summary>
    /// <param name="steps">The flat step plans, in any order — they are
    /// sorted by <see cref="DeploymentStepPlan.Index"/> internally.</param>
    /// <param name="triggerByIndex">Lookup returning the <see cref="StepStartTrigger"/>
    /// for a given plan's <see cref="DeploymentStepPlan.Index"/>. The first
    /// step's trigger is ignored (a first step has no predecessor to wait
    /// for or run alongside) — passing
    /// <see cref="StepStartTrigger.StartAfterPrevious"/> for it is the
    /// canonical choice.</param>
    public static List<Wave> Partition(
        IReadOnlyList<DeploymentStepPlan> steps,
        Func<int, StepStartTrigger> triggerByIndex)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(triggerByIndex);

        var waves = new List<Wave>();
        if (steps.Count == 0)
        {
            return waves;
        }

        var ordered = steps.OrderBy(s => s.Index).ToArray();

        // Start the first wave with the first step. The first step's
        // StartTrigger is always ignored — a step at SortOrder == 0 cannot
        // run "with the previous step" because there is none.
        var current = new List<DeploymentStepPlan> { ordered[0] };

        for (var i = 1; i < ordered.Length; i++)
        {
            var step    = ordered[i];
            var trigger = triggerByIndex(step.Index);

            if (trigger == StepStartTrigger.StartWithPrevious)
            {
                current.Add(step);
            }
            else
            {
                waves.Add(BuildWave(current));
                current = [step];
            }
        }
        waves.Add(BuildWave(current));

        return waves;
    }

    /// <summary>
    /// Builds the <see cref="Wave"/> record for the accumulated step list,
    /// classifying it as Server or Target and rejecting mixed-side
    /// composition. Single-step waves (the common case under default
    /// <see cref="StepStartTrigger.StartAfterPrevious"/>) trivially pass the
    /// homogeneity check.
    /// </summary>
    private static Wave BuildWave(IReadOnlyList<DeploymentStepPlan> steps)
    {
        var serverNames = new List<string>();
        var targetNames = new List<string>();

        foreach (var step in steps)
        {
            if (IsServerStep(step))
            {
                serverNames.Add(step.Name);
            }
            else
            {
                targetNames.Add(step.Name);
            }
        }

        if (serverNames.Count > 0 && targetNames.Count > 0)
        {
            throw new InvalidWaveException(
                $"Wave [{string.Join(", ", steps.Select(s => s.Name))}] mixes " +
                $"server-side steps ({string.Join(", ", serverNames)}) with " +
                $"target-side steps ({string.Join(", ", targetNames)}). " +
                "Mixed waves are not supported (M14.4): split the wave into " +
                "two single-side parallel waves run sequentially.",
                steps,
                serverNames,
                targetNames);
        }

        return new Wave(
            serverNames.Count > 0 ? WaveKind.Server : WaveKind.Target,
            steps);
    }

    /// <summary>
    /// Whether a step runs server-side. A step is server-side when EITHER
    /// the config carries <c>Octopus.Action.RunOnServer = "true"</c> (the
    /// explicit Octopus marker), OR the <see cref="DeploymentStepPlan.StepType"/>
    /// is one of the intrinsically server-side orchestrator types in
    /// <see cref="ServerOnlyStepTypes"/>.
    ///
    /// <para>
    /// Moved here from <c>DeploymentWorker</c> (M14.0..3) so the partitioner
    /// can classify in isolation and unit tests don't need an orchestrator
    /// fixture. The orchestrator still reads through this helper when it
    /// needs to know a single step's side outside the wave path.
    /// </para>
    /// </summary>
    internal static bool IsServerStep(DeploymentStepPlan step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Config.TryGetValue("Octopus.Action.RunOnServer", out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return ServerOnlyStepTypes.Contains(step.StepType);
    }

    /// <summary>
    /// Step types that always run on the server regardless of the
    /// <c>Octopus.Action.RunOnServer</c> flag — they coordinate other
    /// deployments or otherwise have no agent-side meaning. Mirrors the
    /// set previously defined inline in <c>DeploymentWorker</c>.
    /// </summary>
    internal static readonly HashSet<string> ServerOnlyStepTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            DeployReleaseStepRunner.StepType,
        };
}

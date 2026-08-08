using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Execution;
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
        /// <summary>Every step in the wave runs server-side (the typed
        /// <c>RunOnServer</c> flag, or an intrinsically server-only StepType like
        /// Octopus.DeployRelease).</summary>
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
    /// <param name="serverSideTypes">Step types that execute server-side
    /// regardless of the per-step <see cref="DeploymentStepPlan.RunOnServer"/>
    /// flag. SC4-b: sourced from the step-type registry (rows whose
    /// <c>ExecutionLocus</c> is not AgentPackage) — the packages themselves
    /// declare it; there is no hardcoded list anymore.</param>
    public static List<Wave> Partition(
        IReadOnlyList<DeploymentStepPlan> steps,
        Func<int, StepStartTrigger> triggerByIndex,
        IReadOnlySet<string> serverSideTypes)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(triggerByIndex);
        ArgumentNullException.ThrowIfNull(serverSideTypes);

        // Trigger-based grouping is shared with the offline agent runner via
        // KrakenDeploy.Execution's WaveGrouping (single source of truth for the
        // "first step + StartWithPrevious run until StartAfterPrevious" rule +
        // the index ordering). The first step's trigger is ignored (the helper
        // only evaluates the predicate for steps after the first).
        var groups = WaveGrouping.Partition(
            steps,
            s => s.Index,
            s => triggerByIndex(s.Index) == StepStartTrigger.StartWithPrevious);

        // Classification (server-side vs target-side) + mixed-wave validation
        // stay here — they are online-only concerns the agent does not share.
        var waves = new List<Wave>(groups.Count);
        foreach (var group in groups)
        {
            waves.Add(BuildWave(group, serverSideTypes));
        }

        return waves;
    }

    /// <summary>
    /// Builds the <see cref="Wave"/> record for the accumulated step list,
    /// classifying it as Server or Target and rejecting mixed-side
    /// composition. Single-step waves (the common case under default
    /// <see cref="StepStartTrigger.StartAfterPrevious"/>) trivially pass the
    /// homogeneity check.
    /// </summary>
    private static Wave BuildWave(
        IReadOnlyList<DeploymentStepPlan> steps, IReadOnlySet<string> serverSideTypes)
    {
        var serverNames = new List<string>();
        var targetNames = new List<string>();

        foreach (var step in steps)
        {
            if (IsServerStep(step, serverSideTypes))
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
    /// the typed <see cref="DeploymentStepPlan.RunOnServer"/> flag is set (D3 —
    /// promoted from the <c>Octopus.Action.RunOnServer</c> Config key), OR the
    /// <see cref="DeploymentStepPlan.StepType"/> is in
    /// <paramref name="serverSideTypes"/>.
    ///
    /// <para>
    /// SC4-b: the intrinsically server-side set comes from the step-type
    /// registry (locus declared by the packages themselves — e.g.
    /// <c>Octopus.Manual</c>'s manifest says <c>executionLocus=server</c>
    /// because a manual-intervention gate is TASK-GLOBAL, and
    /// <c>Octopus.DeployRelease</c> is a System registry row). The old
    /// hardcoded <c>ServerOnlyStepTypes</c> constant is gone; callers load
    /// the set once per dispatch.
    /// </para>
    /// </summary>
    internal static bool IsServerStep(
        DeploymentStepPlan step, IReadOnlySet<string> serverSideTypes)
    {
        ArgumentNullException.ThrowIfNull(step);
        // D3 — RunOnServer is a typed field on the plan now (promoted from the
        // Config key "Octopus.Action.RunOnServer"). The flattener stamps it from
        // StepSnapshot.RunOnServer; the raw key no longer travels in Config.
        return step.RunOnServer || serverSideTypes.Contains(step.StepType);
    }
}

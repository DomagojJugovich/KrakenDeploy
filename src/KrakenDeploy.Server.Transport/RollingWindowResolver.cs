using System.Globalization;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M-RollingDeployments Phase 2 — resolves the effective rolling-window
/// (<c>Octopus.Action.MaxParallelism</c>) cap for a wave by walking each
/// step's <see cref="StepSnapshot.ParentStepId"/> chain up to the nearest
/// ancestor <see cref="KrakenStepTypes.StepGroup"/> with a parseable
/// positive integer in <c>Config["Octopus.Action.MaxParallelism"]</c>.
///
/// <para>
/// <strong>Semantic:</strong> batches a wave's target fan-out into windows
/// of N when N &lt; <c>deployment.Targets.Count</c>. The windows run
/// SEQUENTIALLY (targets within a window run in parallel), so N is a
/// concurrency / blast-radius cap. It is NOT a canary gate: a Required
/// failure inside window K does NOT stop windows K+1..end — every window of
/// the wave runs. The failing target is dropped, but that drop / soft-
/// failure is applied to the alive set only at the NEXT wave (the per-target
/// drop-out in <c>DeploymentWorker</c>), not between windows — condition
/// evaluation for the whole wave happens up front, before batching. Between
/// waves the standard sequential barrier applies; server-side waves ignore
/// MaxParallelism and run once regardless.
/// </para>
///
/// <para>
/// <strong>Canary:</strong> to stop a rollout when an early target fails,
/// model the validating step as a SEPARATE EARLIER wave (its own barrier)
/// before the rolling group — its failure drops the target (or, in the
/// Atomic <c>DeploymentFailureMode</c>, flips the deployment-global failing
/// state) before the rolling wave starts. Full group-region canary (every
/// wave of a rolling group completes on window K before window K+1 starts)
/// could layer on later without changing this resolver. Imported Octopus
/// rolling steps land in this same shape; the cap value carries over verbatim.
/// </para>
/// </summary>
public static class RollingWindowResolver
{
    /// <summary>
    /// The Config key Octopus uses on a step / step group to cap target-
    /// fan-out concurrency. Kraken reads it from a <see cref="KrakenStepTypes.StepGroup"/>'s
    /// snapshot Config (preserved verbatim by the importer + UI).
    /// </summary>
    public const string MaxParallelismKey = "Octopus.Action.MaxParallelism";

    /// <summary>
    /// Returns the effective per-wave fan-out cap when EVERY step in the
    /// wave shares the same rolling ancestor (nearest <see cref="KrakenStepTypes.StepGroup"/>
    /// with <see cref="MaxParallelismKey"/> set to a positive integer).
    /// Returns <c>null</c> when:
    /// <list type="bullet">
    ///   <item>no step in the wave has a rolling ancestor</item>
    ///   <item>different steps in the wave have different rolling ancestors
    ///         (treated as "no batching" to avoid surprising operators with
    ///         a partial cap)</item>
    ///   <item>the resolved value isn't a positive integer (malformed
    ///         config falls back to "no cap" so a typo can't accidentally
    ///         serialise the deployment to one-target-at-a-time)</item>
    /// </list>
    ///
    /// <para>
    /// <paramref name="snapshotById"/> indexes the FULL process snapshot
    /// (not just emitted plans) because rolling parents are usually
    /// container <see cref="KrakenStepTypes.StepGroup"/> rows that don't
    /// appear in the flat plan list — the flattener emits children only.
    /// </para>
    /// </summary>
    public static int? ResolveWaveMaxParallelism(
        IReadOnlyList<DeploymentStepPlan> waveSteps,
        IReadOnlyList<StepSnapshot> snapshotByPlanIndex,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById)
    {
        ArgumentNullException.ThrowIfNull(waveSteps);
        ArgumentNullException.ThrowIfNull(snapshotByPlanIndex);
        ArgumentNullException.ThrowIfNull(snapshotById);

        if (waveSteps.Count == 0)
        {
            return null;
        }

        Guid? sharedAncestor = null;
        int? sharedCap = null;
        foreach (var plan in waveSteps)
        {
            if (plan.Index < 0 || plan.Index >= snapshotByPlanIndex.Count)
            {
                return null;
            }
            var snap = snapshotByPlanIndex[plan.Index];
            var (ancestorId, cap) = ResolveRollingAncestor(snap, snapshotById);
            if (ancestorId is null || cap is null)
            {
                return null; // at least one step has no rolling cap — give up
            }
            if (sharedAncestor is null)
            {
                sharedAncestor = ancestorId;
                sharedCap = cap;
            }
            else if (sharedAncestor != ancestorId)
            {
                return null; // mixed rolling groups within one wave — no batching
            }
        }
        return sharedCap;
    }

    /// <summary>
    /// Resolves the rolling ancestor's name (for audit detail). Mirrors
    /// <see cref="ResolveWaveMaxParallelism"/>'s walk; returns null when
    /// no shared rolling ancestor applies.
    /// </summary>
    public static string? ResolveWaveRollingGroupName(
        IReadOnlyList<DeploymentStepPlan> waveSteps,
        IReadOnlyList<StepSnapshot> snapshotByPlanIndex,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById)
    {
        ArgumentNullException.ThrowIfNull(waveSteps);
        ArgumentNullException.ThrowIfNull(snapshotByPlanIndex);
        ArgumentNullException.ThrowIfNull(snapshotById);

        if (waveSteps.Count == 0)
        {
            return null;
        }
        Guid? sharedAncestor = null;
        foreach (var plan in waveSteps)
        {
            if (plan.Index < 0 || plan.Index >= snapshotByPlanIndex.Count)
            {
                return null;
            }
            var snap = snapshotByPlanIndex[plan.Index];
            var (ancestorId, cap) = ResolveRollingAncestor(snap, snapshotById);
            if (ancestorId is null || cap is null)
            {
                return null;
            }
            if (sharedAncestor is null)
            {
                sharedAncestor = ancestorId;
            }
            else if (sharedAncestor != ancestorId)
            {
                return null;
            }
        }
        return sharedAncestor is not null
                && snapshotById.TryGetValue(sharedAncestor.Value, out var groupSnap)
            ? groupSnap.Name
            : null;
    }

    /// <summary>
    /// Splits <paramref name="targets"/> into contiguous batches of at
    /// most <paramref name="maxParallelism"/> in declared order. When
    /// <paramref name="maxParallelism"/> is &lt;= 0 OR &gt;= the target
    /// count, returns a single batch with every target so the caller can
    /// short-circuit batching (it's still useful to call this helper to
    /// keep one return shape).
    /// </summary>
    public static List<List<T>> Chunk<T>(IReadOnlyList<T> targets, int maxParallelism)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            return [];
        }
        if (maxParallelism <= 0 || maxParallelism >= targets.Count)
        {
            return [[.. targets]];
        }
        var batches = new List<List<T>>();
        for (var i = 0; i < targets.Count; i += maxParallelism)
        {
            var batch = new List<T>(maxParallelism);
            for (var j = i; j < Math.Min(i + maxParallelism, targets.Count); j++)
            {
                batch.Add(targets[j]);
            }
            batches.Add(batch);
        }
        return batches;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static (Guid? AncestorId, int? Cap) ResolveRollingAncestor(
        StepSnapshot start,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById)
    {
        // Walk up the ParentStepId chain. A step might itself be a
        // StepGroup with MaxParallelism — in which case its OWN row is
        // the rolling ancestor (operators can author a leaf-shaped
        // composite step at the top level — rare, but valid).
        var current = start;
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (IsRollingStepGroup(current, out var cap))
            {
                return (current.Id, cap);
            }
            if (current.ParentStepId is null
                || current.ParentStepId.Value == Guid.Empty
                || !snapshotById.TryGetValue(current.ParentStepId.Value, out var parent)
                || !visited.Add(current.Id))
            {
                return (null, null);
            }
            current = parent;
        }
    }

    private static bool IsRollingStepGroup(StepSnapshot snap, out int? cap)
    {
        cap = null;
        if (!string.Equals(snap.StepType, KrakenStepTypes.StepGroup,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!snap.Config.TryGetValue(MaxParallelismKey, out var raw))
        {
            return false;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || n <= 0)
        {
            return false; // malformed value falls back to "no cap" rather than 1-at-a-time
        }
        cap = n;
        return true;
    }
}

using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M-RollingDeployments Phase 2 — resolves the effective rolling-window cap for
/// a wave by walking each step's <see cref="StepSnapshot.ParentStepId"/> chain
/// up to the nearest ancestor <see cref="KrakenStepTypes.StepGroup"/> that
/// declares a rolling window (D3: the typed <see cref="StepSnapshot.MaxParallelism"/>
/// column, no longer the <c>Octopus.Action.MaxParallelism</c> Config key).
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
/// <strong>Visibility (D3 RIDER):</strong> the resolver returns a
/// <see cref="RollingCapReason"/> alongside the cap so <c>DeploymentWorker</c>
/// can surface WHY batching was disabled when a rolling group is present:
/// <see cref="RollingCapReason.Malformed"/> (a non-positive cap that slipped in
/// via import / legacy data — the typed column kills this at save time going
/// forward) or <see cref="RollingCapReason.MixedAncestors"/> (the wave's steps
/// don't all belong to one rolling group). The no-cap fallback is deliberate —
/// serialising to one-target-at-a-time would be worse — but it is now audible.
/// </para>
///
/// <para>
/// <paramref name="snapshotById"/> indexes the FULL process snapshot (not just
/// emitted plans) because rolling parents are container
/// <see cref="KrakenStepTypes.StepGroup"/> rows that don't appear in the flat
/// plan list — the flattener emits children only.
/// </para>
/// </summary>
public static class RollingWindowResolver
{
    /// <summary>
    /// Resolves the effective rolling window for a wave: the cap plus a reason
    /// describing why the cap is / isn't set. See the type docs for the
    /// batching semantic.
    /// <list type="bullet">
    ///   <item><see cref="RollingCapReason.None"/> — no step in the wave has a
    ///         rolling ancestor (the common, non-rolling wave). Silent.</item>
    ///   <item><see cref="RollingCapReason.Resolved"/> — every step shares one
    ///         rolling ancestor with a positive cap; <see cref="RollingWindow.Cap"/>
    ///         is that value.</item>
    ///   <item><see cref="RollingCapReason.Malformed"/> — the shared rolling
    ///         ancestor's cap is non-positive (imported / legacy). Cap is null
    ///         (no batching) and the caller warns.</item>
    ///   <item><see cref="RollingCapReason.MixedAncestors"/> — the wave's steps
    ///         reference different rolling ancestors, or mix rolling and
    ///         non-rolling steps. Cap is null (no batching) and the caller warns.</item>
    /// </list>
    /// </summary>
    public static RollingWindow ResolveWaveRollingWindow(
        IReadOnlyList<DeploymentStepPlan> waveSteps,
        IReadOnlyList<StepSnapshot> snapshotByPlanIndex,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById)
    {
        ArgumentNullException.ThrowIfNull(waveSteps);
        ArgumentNullException.ThrowIfNull(snapshotByPlanIndex);
        ArgumentNullException.ThrowIfNull(snapshotById);

        if (waveSteps.Count == 0)
        {
            return RollingWindow.NoCap;
        }

        Guid? sharedAncestor = null;
        int? sharedCap = null;
        var sharedMalformed = false;
        var anyWithoutAncestor = false;

        foreach (var plan in waveSteps)
        {
            if (plan.Index < 0 || plan.Index >= snapshotByPlanIndex.Count)
            {
                return RollingWindow.NoCap; // defensive — index out of range
            }
            var snap = snapshotByPlanIndex[plan.Index];
            var (ancestorId, cap, malformed) = ResolveRollingAncestor(snap, snapshotById);
            if (ancestorId is null)
            {
                anyWithoutAncestor = true;
                continue;
            }
            if (sharedAncestor is null)
            {
                sharedAncestor = ancestorId;
                sharedCap = cap;
                sharedMalformed = malformed;
            }
            else if (sharedAncestor != ancestorId)
            {
                // Different rolling groups within one wave — no batching.
                return new RollingWindow(null, RollingCapReason.MixedAncestors, null);
            }
        }

        if (sharedAncestor is null)
        {
            // No step in the wave has a rolling ancestor — nothing to cap.
            return RollingWindow.NoCap;
        }

        var groupName = snapshotById.TryGetValue(sharedAncestor.Value, out var groupSnap)
            ? groupSnap.Name
            : null;

        if (anyWithoutAncestor)
        {
            // Some steps belong to the rolling group, others to none — the wave
            // isn't a uniform rolling group, so batching is disabled (as before)
            // but now surfaced.
            return new RollingWindow(null, RollingCapReason.MixedAncestors, groupName);
        }

        if (sharedMalformed)
        {
            return new RollingWindow(null, RollingCapReason.Malformed, groupName);
        }

        return new RollingWindow(sharedCap, RollingCapReason.Resolved, groupName);
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

    /// <summary>
    /// Walks up the <see cref="StepSnapshot.ParentStepId"/> chain and returns
    /// the nearest <see cref="KrakenStepTypes.StepGroup"/> that DECLARES a
    /// rolling window (a non-null <see cref="StepSnapshot.MaxParallelism"/>).
    /// That group is the rolling boundary regardless of whether its value is
    /// valid: a positive value yields the cap; a non-positive value yields
    /// <c>Malformed = true</c> (no cap, but the group is still the identified
    /// ancestor so the caller can name it in the warning). Returns
    /// <c>(null, null, false)</c> when no ancestor declares a window.
    /// </summary>
    private static (Guid? AncestorId, int? Cap, bool Malformed) ResolveRollingAncestor(
        StepSnapshot start,
        IReadOnlyDictionary<Guid, StepSnapshot> snapshotById)
    {
        var current = start;
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (string.Equals(current.StepType, KrakenStepTypes.StepGroup,
                    StringComparison.OrdinalIgnoreCase)
                && current.MaxParallelism.HasValue)
            {
                var n = current.MaxParallelism.Value;
                return n > 0
                    ? (current.Id, n, false)
                    : (current.Id, null, true); // non-positive → malformed, no cap
            }
            if (current.ParentStepId is null
                || current.ParentStepId.Value == Guid.Empty
                || !snapshotById.TryGetValue(current.ParentStepId.Value, out var parent)
                || !visited.Add(current.Id))
            {
                return (null, null, false);
            }
            current = parent;
        }
    }
}

/// <summary>Why a wave did / didn't get a rolling-window cap. See
/// <see cref="RollingWindowResolver.ResolveWaveRollingWindow"/>.</summary>
public enum RollingCapReason
{
    /// <summary>No step in the wave has a rolling ancestor. Silent.</summary>
    None,

    /// <summary>A shared rolling ancestor resolved to a positive cap.</summary>
    Resolved,

    /// <summary>A shared rolling ancestor exists but its <c>MaxParallelism</c>
    /// is non-positive (imported / legacy data). Batching disabled; warned.</summary>
    Malformed,

    /// <summary>The wave's steps reference different rolling ancestors, or mix
    /// rolling and non-rolling steps. Batching disabled; warned.</summary>
    MixedAncestors,
}

/// <summary>The resolved rolling window for one wave: the effective per-wave
/// fan-out <see cref="Cap"/> (null when batching is disabled), the
/// <see cref="Reason"/>, and the rolling group's <see cref="RollingGroupName"/>
/// for audit/log detail (null for <see cref="RollingCapReason.None"/> and for
/// pure <see cref="RollingCapReason.MixedAncestors"/> with no single group).</summary>
public sealed record RollingWindow(int? Cap, RollingCapReason Reason, string? RollingGroupName)
{
    /// <summary>The no-cap / no-rolling-group result.</summary>
    public static readonly RollingWindow NoCap = new(null, RollingCapReason.None, null);
}

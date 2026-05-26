using System.Collections.Concurrent;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Result returned by the agent for an in-flight sub-plan.
/// </summary>
public sealed record SubPlanResult(bool Success, string? ErrorMessage);

/// <summary>
/// M14.4 — per-step outcome reported by the agent during a sub-plan's wave.
/// The orchestrator drains the registry's per-step results when the sub-plan
/// completes and uses them to attribute Required failures to the actual
/// failing step (not the conservative whole-wave abort the M14.0..3 code
/// performed).
///
/// <para>
/// <see cref="StepIndex"/> is the plan-level <c>DeploymentStepPlan.Index</c>
/// — stable across the deployment so the worker can look up the
/// <c>StepSnapshot</c> by index without name-collision risk inside ForEach
/// iterations (M15) or duplicate-name authoring.
/// </para>
/// </summary>
public sealed record SubPlanStepResult(
    int StepIndex,
    string StepName,
    bool Success,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> Outputs);

/// <summary>
/// Shared singleton state coordinating piecewise agent dispatches between
/// <see cref="DeploymentWorker"/> (writer) and <see cref="AgentHub"/> (reader).
/// <para>
/// When the worker dispatches a target-side sub-plan it registers a
/// <see cref="TaskCompletionSource{TResult}"/> here keyed by
/// (deployment id, target id), then awaits the task. While the wave runs the
/// agent calls <see cref="IAgentHubServer.ReportStepCompletedAsync"/> per
/// step — the hub forwards those into <see cref="RecordStepResult"/> here
/// (the hub resolves the target id from its connection's NameIdentifier
/// claim, so no wire-contract change) so the worker can drain them on wave
/// completion. When the agent finishes the sub-plan and calls
/// <c>AgentHub.CompleteDeploymentAsync</c>, the hub resolves the TCS so the
/// worker resumes, drains per-step results, and continues to the next wave.
/// </para>
/// <para>
/// M-RollingDeployments Phase 1b: slots are per-(deployment, target) so a
/// multi-target deployment can have N sub-plans in flight concurrently
/// (one per target inside the same wave). The pre-1b key (deployment-only)
/// is reachable through the (deployment, <see cref="Guid.Empty"/>) tuple —
/// the single-target dispatch path and tests that don't care about the
/// target dimension can pass <see cref="Guid.Empty"/>.
/// </para>
/// </summary>
public interface IPendingSubPlanRegistry
{
    /// <summary>
    /// Register a TCS that <see cref="TryResolve"/> will complete when the
    /// agent reports sub-plan completion. Clears any per-step results left
    /// over from a previous wave so the new wave starts with an empty bag.
    /// </summary>
    void Register(Guid deploymentId, Guid targetId, TaskCompletionSource<SubPlanResult> tcs);

    /// <summary>
    /// Resolve a pending TCS if one is registered. Returns <c>true</c> if a
    /// TCS was waiting (caller should NOT finalize the deployment); <c>false</c>
    /// if the deployment was not running a sub-plan (caller should finalize
    /// as usual).
    /// </summary>
    bool TryResolve(Guid deploymentId, Guid targetId, SubPlanResult result);

    /// <summary>
    /// Forcefully cancel any pending TCS for this (deployment, target) pair
    /// (used on cleanup when the worker bails out of a dispatch loop).
    /// </summary>
    void Cancel(Guid deploymentId, Guid targetId, string reason);

    /// <summary>
    /// M14.4 — record a per-step outcome from the agent. Called by
    /// <c>AgentHub.ReportStepCompletedAsync</c>. Silently no-ops if no
    /// sub-plan is currently registered for the (deployment, target) pair
    /// (e.g. the wave already timed out and was cancelled — late reports are
    /// dropped).
    /// </summary>
    void RecordStepResult(Guid deploymentId, Guid targetId, SubPlanStepResult result);

    /// <summary>
    /// M14.4 — drain the per-step outcomes accumulated for the currently
    /// in-flight or just-resolved sub-plan, in arrival order. The orchestrator
    /// calls this after the wave's <see cref="TryResolve"/> fires (success
    /// or failure) to apply per-step Required attribution + emit collision
    /// audits. Returns an empty list when no per-step reports landed.
    /// </summary>
    IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId);

    /// <summary>
    /// M-RollingDeployments Phase 1b — hub-side lookup: which (deployment,
    /// target) slots are awaiting a TCS resolution that this connection's
    /// target id could fulfil. The hub uses this when the agent calls
    /// <c>CompleteDeploymentAsync</c> / <c>ReportStepCompletedAsync</c> with a
    /// deployment id but the per-target slot key was registered on the
    /// orchestrator side. Returns the set of target ids the registry has
    /// open slots for, so the hub can route the agent's report to the right
    /// slot by intersecting with the connection's claimed target id.
    /// </summary>
    bool HasSlot(Guid deploymentId, Guid targetId);
}

public sealed class PendingSubPlanRegistry : IPendingSubPlanRegistry
{
    private readonly ConcurrentDictionary<(Guid DeploymentId, Guid TargetId), TaskCompletionSource<SubPlanResult>> _pending = new();
    // Per-(deployment, target) per-step results bag. Survives until the next
    // Register call clears it, so the orchestrator can drain after TryResolve.
    private readonly ConcurrentDictionary<(Guid DeploymentId, Guid TargetId), List<SubPlanStepResult>> _stepResults = new();

    public void Register(Guid deploymentId, Guid targetId, TaskCompletionSource<SubPlanResult> tcs)
    {
        ArgumentNullException.ThrowIfNull(tcs);
        var key = (deploymentId, targetId);
        // Overwrite any stale entry (should not normally happen — caller
        // guarantees only one sub-plan per (deployment, target) at a time).
        _pending[key] = tcs;
        // New wave starts with a clean per-step bag.
        _stepResults[key] = [];
    }

    public bool TryResolve(Guid deploymentId, Guid targetId, SubPlanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pending.TryRemove((deploymentId, targetId), out var tcs))
        {
            tcs.TrySetResult(result);
            return true;
        }
        return false;
    }

    public void Cancel(Guid deploymentId, Guid targetId, string reason)
    {
        if (_pending.TryRemove((deploymentId, targetId), out var tcs))
        {
            tcs.TrySetResult(new SubPlanResult(false, reason));
        }
    }

    public void RecordStepResult(Guid deploymentId, Guid targetId, SubPlanStepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var key = (deploymentId, targetId);
        // Append iff a sub-plan is currently in flight — otherwise we'd leak
        // unbounded state from late reports of cancelled waves. The
        // _pending check is the in-flight gate; the _stepResults bag is
        // populated by Register so the absence of _pending means "nobody's
        // listening".
        if (!_pending.ContainsKey(key))
        {
            return;
        }
        if (_stepResults.TryGetValue(key, out var list))
        {
            lock (list)
            {
                list.Add(result);
            }
        }
    }

    public IReadOnlyList<SubPlanStepResult> DrainStepResults(Guid deploymentId, Guid targetId)
    {
        if (!_stepResults.TryRemove((deploymentId, targetId), out var list))
        {
            return [];
        }
        lock (list)
        {
            // Defensive copy — list is mutated under lock; caller iterates
            // freely without us holding the lock.
            return list.ToArray();
        }
    }

    public bool HasSlot(Guid deploymentId, Guid targetId)
        => _pending.ContainsKey((deploymentId, targetId));
}

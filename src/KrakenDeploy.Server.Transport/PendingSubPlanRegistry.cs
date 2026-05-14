using System.Collections.Concurrent;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Result returned by the agent for an in-flight sub-plan.
/// </summary>
public sealed record SubPlanResult(bool Success, string? ErrorMessage);

/// <summary>
/// Shared singleton state coordinating piecewise agent dispatches between
/// <see cref="DeploymentWorker"/> (writer) and <see cref="AgentHub"/> (reader).
/// <para>
/// When the worker dispatches a target-side sub-plan it registers a
/// <see cref="TaskCompletionSource{TResult}"/> here keyed by deployment ID,
/// then awaits the task. When the agent finishes that sub-plan and calls
/// <c>AgentHub.CompleteDeploymentAsync</c>, the hub resolves the TCS so the
/// worker resumes and continues to the next group (server or target) of
/// the process. Without a pending TCS, the hub falls through to its existing
/// "finalize the deployment" logic so single-shot deployments behave exactly
/// as before.
/// </para>
/// <para>
/// At most one sub-plan can be in flight per deployment at any time, so a
/// single TCS slot per deployment ID is sufficient.
/// </para>
/// </summary>
public interface IPendingSubPlanRegistry
{
    /// <summary>
    /// Register a TCS that <see cref="TryResolve"/> will complete when the
    /// agent reports sub-plan completion.
    /// </summary>
    void Register(Guid deploymentId, TaskCompletionSource<SubPlanResult> tcs);

    /// <summary>
    /// Resolve a pending TCS if one is registered. Returns <c>true</c> if a
    /// TCS was waiting (caller should NOT finalize the deployment); <c>false</c>
    /// if the deployment was not running a sub-plan (caller should finalize
    /// as usual).
    /// </summary>
    bool TryResolve(Guid deploymentId, SubPlanResult result);

    /// <summary>
    /// Forcefully cancel any pending TCS for this deployment (used on cleanup
    /// when the worker bails out of a dispatch loop).
    /// </summary>
    void Cancel(Guid deploymentId, string reason);
}

public sealed class PendingSubPlanRegistry : IPendingSubPlanRegistry
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SubPlanResult>> _pending = new();

    public void Register(Guid deploymentId, TaskCompletionSource<SubPlanResult> tcs)
    {
        ArgumentNullException.ThrowIfNull(tcs);
        // Overwrite any stale entry (should not normally happen — caller
        // guarantees only one sub-plan per deployment at a time).
        _pending[deploymentId] = tcs;
    }

    public bool TryResolve(Guid deploymentId, SubPlanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pending.TryRemove(deploymentId, out var tcs))
        {
            tcs.TrySetResult(result);
            return true;
        }
        return false;
    }

    public void Cancel(Guid deploymentId, string reason)
    {
        if (_pending.TryRemove(deploymentId, out var tcs))
        {
            tcs.TrySetResult(new SubPlanResult(false, reason));
        }
    }
}

namespace KrakenDeploy.Server.Core.Domain.Deployments;

public enum DeploymentStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    PendingOfflineResult = 5,

    /// <summary>
    /// M14.2 — deployment completed but at least one non-required step
    /// failed. Mirrors Octopus's yellow-badge state. Operators reading
    /// the deployment list see "ran but had a hiccup" without losing the
    /// terminal-success signal. Distinct from <see cref="Failed"/> which
    /// means a required step aborted the run.
    /// </summary>
    SucceededWithWarnings = 6,

    /// <summary>
    /// WP3 — the task is parked at a manual-intervention gate awaiting a human
    /// approve/reject. NON-terminal, and deliberately lease-less: the worker
    /// persists an execution checkpoint, frees its <c>NodeTaskGate</c> slot and
    /// returns, so no thread or capacity is held across the approval window
    /// (which defaults to 72 h). Resume is driven by
    /// <c>ServerTaskLease.TryResumeAsync</c> (<c>Paused → Running</c>).
    /// <para>
    /// Like <see cref="PendingOfflineResult"/>, a paused task still HOLDS its F1
    /// (project, environment, tenant) slot — see
    /// <c>DeploymentStatusExtensions.InFlightAfterClaim</c>. Releasing it would let
    /// a newer release deploy and complete while an older one waits for approval,
    /// after which the approved older release would overwrite newer code.
    /// </para>
    /// </summary>
    Paused = 7,
}

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
}

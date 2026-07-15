namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// The single authority for which <see cref="DeploymentStatus"/> values are
/// TERMINAL. Before B1 this classification was duplicated inline (and had
/// already diverged between call sites); every new guard should use this.
/// <c>PendingOfflineResult</c> is deliberately NON-terminal — the task is
/// parked awaiting an out-of-band result bundle.
/// </summary>
public static class DeploymentStatusExtensions
{
    public static bool IsTerminal(this DeploymentStatus status) => status is
        DeploymentStatus.Succeeded or
        DeploymentStatus.SucceededWithWarnings or
        DeploymentStatus.Failed or
        DeploymentStatus.Cancelled;
}

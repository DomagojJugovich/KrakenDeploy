namespace KrakenDeploy.Server.Core.Domain.Deployments;

public enum DeploymentStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    PendingOfflineResult = 5
}

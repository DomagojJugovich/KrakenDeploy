namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Methods the server pushes to connected browser UI clients via <see cref="UiHub"/>.
/// </summary>
public interface IUiHubClient
{
    /// <summary>
    /// Notifies the browser that a deployment target's status has changed.
    /// </summary>
    Task TargetStatusChangedAsync(Guid targetId, string status, DateTimeOffset? lastSeenUtc);

    /// <summary>
    /// Notifies the browser of a new log line from a running deployment.
    /// </summary>
    Task DeploymentLogAppendedAsync(
        Guid deploymentId, int sequence, DateTimeOffset timestamp,
        string level, string message);

    /// <summary>
    /// Notifies the browser that a deployment's overall status has changed
    /// (e.g. Queued → Running → Succeeded).
    /// </summary>
    Task DeploymentStatusChangedAsync(Guid deploymentId, string status);
}

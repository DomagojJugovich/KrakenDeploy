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
}

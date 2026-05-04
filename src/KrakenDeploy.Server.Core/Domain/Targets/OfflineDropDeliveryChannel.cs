namespace KrakenDeploy.Server.Core.Domain.Targets;

/// <summary>
/// How the server delivers an offline drop bundle to the target environment.
/// </summary>
public enum OfflineDropDeliveryChannel
{
    /// <summary>User downloads the bundle from the UI and transfers it manually.</summary>
    Manual = 0,

    /// <summary>Server emails the bundle (or a link) to a configured recipient.</summary>
    Email = 1,

    /// <summary>Server POSTs the bundle to a configured webhook URL.</summary>
    Webhook = 2,

    /// <summary>Server copies the bundle to a network file share or SFTP path.</summary>
    FileShareDrop = 3,
}

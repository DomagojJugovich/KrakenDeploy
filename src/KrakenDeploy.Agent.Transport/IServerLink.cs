using KrakenDeploy.Contracts;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Abstraction over the SignalR control-plane connection to the KrakenDeploy server.
/// </summary>
public interface IServerLink : IAsyncDisposable
{
    /// <summary>Whether the hub connection is currently in the Connected state.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the connection and authenticates using <paramref name="agentJwt"/>.</summary>
    Task StartAsync(string serverUrl, string agentJwt, CancellationToken ct);

    /// <summary>Gracefully stops the hub connection.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Sends full machine info to the server hub.</summary>
    Task RegisterAsync(AgentRegistrationRequest request, CancellationToken ct);

    /// <summary>Sends a periodic heartbeat with optional updated machine info.</summary>
    Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct);

    /// <summary>Reports a text status string (e.g. "ShuttingDown") to the server hub.</summary>
    Task ReportStatusAsync(string status, CancellationToken ct);
}

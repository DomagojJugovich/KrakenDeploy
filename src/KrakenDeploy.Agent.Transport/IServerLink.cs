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

    // ── Agent → Server ─────────────────────────────────────────────────────

    /// <summary>Sends full machine info to the server hub.</summary>
    Task RegisterAsync(AgentRegistrationRequest request, CancellationToken ct);

    /// <summary>Sends a periodic heartbeat with optional updated machine info.</summary>
    Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct);

    /// <summary>Reports a text status string (e.g. "ShuttingDown") to the server hub.</summary>
    Task ReportStatusAsync(string status, CancellationToken ct);

    /// <summary>Sends a single log line from an executing deployment step to the server.</summary>
    Task AppendLogAsync(Guid deploymentId, string level, string message, CancellationToken ct);

    /// <summary>Reports deployment completion (success or failure) to the server.</summary>
    Task CompleteDeploymentAsync(
        Guid deploymentId, bool success, string? errorMessage, CancellationToken ct);

    /// <summary>
    /// Reports the output variables captured for a step via Octopus-compatible
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> markers.
    /// Server persists them and surfaces them as
    /// <c>Octopus.Action[StepName].Output.X</c> on the deployment detail page.
    /// </summary>
    Task ReportStepOutputVariablesAsync(
        Guid deploymentId, string stepName,
        IReadOnlyDictionary<string, string> outputVariables, CancellationToken ct);

    // ── Server → Agent (subscriptions) ────────────────────────────────────

    /// <summary>
    /// Registers a handler for the <c>RunDeploymentAsync</c> server-push message.
    /// Must be called before <see cref="StartAsync"/> so the handler is wired up
    /// before the connection is opened.
    /// </summary>
    void OnRunDeployment(Func<DeploymentPlan, Task> handler);
}

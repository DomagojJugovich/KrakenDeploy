using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;

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
    /// M14.4 — reports per-step boundary: success/failure outcome, optional
    /// error message, and any output variables captured via
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> markers.
    /// Replaces the pre-M14.4 variable-only reporting (the orchestrator
    /// now needs per-step attribution to apply the Required gate against
    /// individual steps inside a parallel wave).
    /// </summary>
    Task ReportStepCompletedAsync(
        Guid deploymentId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        IReadOnlyDictionary<string, string> outputVariables,
        CancellationToken ct);

    /// <summary>
    /// M11.E.7 — reports an ad-hoc script's outcome (or refusal) back to the
    /// server. Always called exactly once per <see cref="OnRunAdhocScript"/>
    /// invocation, even on the agent's refuse-to-run paths (signature mismatch,
    /// missing key, …) — the server's TCS slot is waiting and must always be
    /// resolved.
    /// </summary>
    Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct);

    // ── Server → Agent (subscriptions) ────────────────────────────────────

    /// <summary>
    /// Registers a handler for the <c>RunDeploymentAsync</c> server-push message.
    /// Must be called before <see cref="StartAsync"/> so the handler is wired up
    /// before the connection is opened.
    /// </summary>
    void OnRunDeployment(Func<DeploymentPlan, Task> handler);

    /// <summary>
    /// M11.E.7 — registers a handler for the <c>RunAdhocScriptAsync</c>
    /// server-push message. Must be called before <see cref="StartAsync"/>.
    /// The handler MUST verify the signature before executing the script.
    /// </summary>
    void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler);
}

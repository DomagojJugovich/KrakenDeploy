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

    /// <summary>
    /// Opens the connection, authenticating with the token returned by
    /// <paramref name="agentJwtProvider"/>. A PROVIDER (not a snapshot) so
    /// automatic reconnects always present the CURRENT token — the sliding
    /// refresh (A8) replaces the token at half-life, and a long-lived process
    /// reconnecting after the original token's expiry must not replay it.
    /// <paramref name="releaseId"/> is the blue-green version pin captured at
    /// registration (multi-node SaaS; null everywhere else) — sent as the
    /// <c>X-KD-Release</c> header so a mid-drain reconnect lands back on the slot
    /// that holds this agent's in-flight orchestration state. A stale pin is
    /// harmless: the router falls back to the current default release.
    /// <para>
    /// B2: re-entrant. Calling it again after a permanent close tears down the
    /// previous connection and opens a fresh one — the supervisor in
    /// <c>ServerLinkHostedService</c> relies on this to restart the link without
    /// restarting the process. Transient drops never need it: the connection's
    /// own unbounded retry policy (<see cref="AgentReconnectPolicy"/>) handles those.
    /// </para>
    /// </summary>
    Task StartAsync(string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct);

    /// <summary>Gracefully stops the hub connection.</summary>
    Task StopAsync(CancellationToken ct);

    // ── Agent → Server ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends full machine info to the server hub and returns the server's
    /// verdict (B6). A refusal (<see cref="AgentRegistrationResult.Accepted"/>
    /// = false — contract-version mismatch) means the server has already
    /// removed this connection from its dispatch registry; the caller must
    /// stop the link and retry on the slow lane.
    /// </summary>
    Task<AgentRegistrationResult> RegisterAsync(AgentRegistrationRequest request, CancellationToken ct);

    /// <summary>Sends a periodic heartbeat with optional updated machine info.</summary>
    Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct);

    /// <summary>Reports a text status string (e.g. "ShuttingDown") to the server hub.</summary>
    Task ReportStatusAsync(string status, CancellationToken ct);

    /// <summary>Sends a single log line from an executing step to the server.
    /// <paramref name="stepIndex"/> is the plan-level step index the line belongs
    /// to (-1 for plan-level lines) so the server can compact logs per step.
    /// <paramref name="dispatchId"/> echoes <c>DeploymentPlan.DispatchId</c> (B6)
    /// so the server can drop lines from a positively-retired dispatch attempt;
    /// <see cref="Guid.Empty"/> for plan-less lines is always accepted.</summary>
    Task AppendLogAsync(
        Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message,
        CancellationToken ct);

    /// <summary>Reports deployment completion (success or failure) to the server.
    /// <paramref name="dispatchId"/> echoes <c>DeploymentPlan.DispatchId</c> so the
    /// server matches the completion to the dispatch attempt that produced it
    /// (B2 — stale/duplicate completions are swallowed server-side).</summary>
    Task CompleteDeploymentAsync(
        Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct);

    /// <summary>
    /// F2 — reports that this dispatch attempt has ACQUIRED the machine execution
    /// gate and is executing now, so the server arms the wave deadline from gate
    /// acquisition instead of from dispatch (queue time behind a busy target no
    /// longer burns the wave's budget). Advisory: a lost report only degrades the
    /// wave to the server's dispatch-time backstop ceiling, never to a wrong
    /// verdict — which is why the outbox may drop it as poison rather than let it
    /// head-of-line-block a verdict.
    /// </summary>
    Task ReportExecutionStartedAsync(Guid deploymentId, Guid dispatchId, CancellationToken ct);

    /// <summary>
    /// M14.4 — reports per-step boundary: success/failure outcome, optional
    /// error message, and any output variables captured via
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> markers.
    /// Replaces the pre-M14.4 variable-only reporting (the orchestrator
    /// now needs per-step attribution to apply the Required gate against
    /// individual steps inside a parallel wave).
    /// <para>
    /// T0-6: <paramref name="sensitiveOutputNames"/> is the subset of
    /// <paramref name="outputVariables"/> keys emitted with
    /// <c>Set-OctopusVariable -sensitive</c>. The server encrypts those values
    /// at rest and masks them in the UI. Empty/null means none are sensitive.
    /// </para>
    /// </summary>
    Task ReportStepCompletedAsync(
        Guid deploymentId,
        Guid dispatchId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        IReadOnlyDictionary<string, string> outputVariables,
        IReadOnlyCollection<string> sensitiveOutputNames,
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

    /// <summary>
    /// B6 — registers a handler for the <c>CancelDeploymentAsync</c> server-push
    /// message (cooperative abort of an in-flight task; taskId covers both
    /// deployments and runbook runs). Must be called before <see cref="StartAsync"/>.
    /// </summary>
    void OnCancelDeployment(Func<Guid, string?, Task> handler);

    // ── Connection lifecycle (subscriptions) ──────────────────────────────

    /// <summary>
    /// B2 — registers a handler invoked when the connection closes PERMANENTLY:
    /// the retry policy stopped (never, with <see cref="AgentReconnectPolicy"/>)
    /// or the server ended the connection in a way automatic reconnect does not
    /// cover. NOT invoked for transient drops the automatic reconnect is still
    /// retrying, nor for closes the agent initiated itself
    /// (<see cref="StopAsync"/> / re-entrant <see cref="StartAsync"/> / dispose).
    /// The supervisor restarts the connection cycle from this signal.
    /// </summary>
    void OnClosed(Func<Exception?, Task> handler);

    /// <summary>
    /// B2 — registers a handler invoked after the automatic reconnect
    /// re-establishes the connection. The server treats a reconnect as a brand
    /// new connection (its <c>OnConnectedAsync</c> re-marks the target Online);
    /// this hook lets the agent re-send registration and flush buffered reports.
    /// </summary>
    void OnReconnected(Func<Task> handler);

    /// <summary>
    /// Registers a handler told whether the server is currently refusing this agent's wire
    /// contract with 426. Raised with <c>true</c> when a reconnect attempt is refused that way
    /// and with <c>false</c> once one is not.
    /// <para>
    /// Exists because a 426 met during AUTOMATIC RECONNECT is invisible everywhere else: the
    /// retry policy never gives up, so <see cref="OnClosed"/> never fires and the supervisor's
    /// own <see cref="StartAsync"/> catch — the other place a refusal is detected — is never
    /// re-entered. That is the path a server upgrade takes for every already-connected agent,
    /// so without this the self-upgrade escape hatch never opened when it was most needed.
    /// </para>
    /// </summary>
    void OnContractRefused(Action<bool> handler);
}

using System.Globalization;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// SignalR implementation of <see cref="IServerLink"/>.
/// <para>
/// The token is delivered via the <c>AccessTokenProvider</c> delegate, which puts the JWT in
/// the query string (<c>?access_token=…</c>) — matching what the server's
/// <c>JwtBearerEvents.OnMessageReceived</c> expects. That is a SignalR convention for the
/// bearer token specifically, not a limitation of the transport: custom request headers set on
/// <c>HttpConnectionOptions</c> ride EVERY hub request, which is what the wire-contract header
/// <c>X-KD-Contract</c> and the blue-green release pin <c>X-KD-Release</c> both rely on.
/// Verified over a real loopback Kestrel by
/// <c>TransportRoundTripTests.The_contract_header_rides_every_hub_request_on_the_negotiated_transport</c>
/// — the gate is mounted on both hub endpoints, so a header missing from either would refuse
/// every connection in that suite. (An earlier revision of this comment claimed the opposite,
/// that "WebSocket upgrades cannot carry custom headers", which would have made the whole
/// design impossible. Note also that the hub is not currently on WebSockets at all — see the
/// residual in <c>docs/agent-wire-contract.md</c>.)
/// </para>
/// </summary>
public sealed class SignalRServerLink : IServerLink
{
    private readonly ILogger<SignalRServerLink> logger;
    private HubConnection? _connection;

    // Set before an agent-initiated teardown (StopAsync / re-entrant StartAsync /
    // DisposeAsync) so the Closed event can tell a deliberate stop from a failure
    // the supervisor must react to.
    private volatile bool _deliberateStop;

    // Handlers registered before StartAsync; wired onto _connection in StartAsync.
    private readonly List<Func<DeploymentPlan, Task>> _deploymentHandlers = [];
    private readonly List<Func<AdhocScriptCommand, Task>> _adhocHandlers = [];
    private readonly List<Func<Guid, string?, Task>> _cancelHandlers = [];
    private readonly List<Func<Exception?, Task>> _closedHandlers = [];
    private readonly List<Func<Task>> _reconnectedHandlers = [];

    // B2 — at-least-once FIFO buffer for work-result reports (logs, step /
    // deployment completions, adhoc results). Survives connection replacement:
    // the pump reads the CURRENT _connection at each send. Started lazily on
    // the first StartAsync; runs until DisposeAsync.
    private readonly ServerLinkOutbox _outbox;
    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _pumpTask;

    public SignalRServerLink(ILogger<SignalRServerLink> logger)
    {
        this.logger = logger;
        _outbox = new ServerLinkOutbox(SendOutboxItemAsync, () => IsConnected, logger);
    }

    // ── IServerLink ────────────────────────────────────────────────────────

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task StartAsync(string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentNullException.ThrowIfNull(agentJwtProvider);

        // B2: re-entrant. Tear down any previous connection first so a
        // supervisor restart never leaks the old one — and so its events can
        // no longer reach the handlers (the closure guard below double-checks).
        if (_connection is not null)
        {
            var previous = _connection;
            _connection = null;
            _deliberateStop = true;
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Disposing the previous hub connection failed — ignored.");
            }
        }

        _deliberateStop = false;

        // One policy instance per connection cycle. It holds only the auth-failure streak
        // flag now — the connection lifecycle no longer feeds it anything, because a
        // server-side REJECTION never reaches the automatic reconnect at all (it arrives as
        // a permanent Closed; see AgentReconnectPolicy) and is paced by the supervisor.
        var reconnectPolicy = new AgentReconnectPolicy(logger);

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/agent";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // Deliver JWT via query string so it survives the WebSocket upgrade.
                // Resolved through the provider on EVERY (re)connect so a token
                // replaced by the sliding refresh (A8) is picked up — a captured
                // snapshot would replay the original token after its expiry.
                options.AccessTokenProvider = () => Task.FromResult(agentJwtProvider());

                // Blue-green version pin (X-KD-Release) — the per-node router uses
                // it to keep this agent on its release's slot across reconnects
                // (docs/blue-green-slot-deployment.md §3). Options persist across
                // automatic reconnects, so the pin rides every (re)connect.
                if (!string.IsNullOrWhiteSpace(releaseId))
                {
                    options.Headers["X-KD-Release"] = releaseId;
                }

                // Wire-contract version on the HANDSHAKE, so the server refuses a skewed
                // agent before the connection is admitted rather than after it is already
                // tracked. Unconditional: an ABSENT header is refused too, so a build that
                // forgets to send it fails loudly instead of being read as compatible.
                // Rides every (re)connect for the same reason the release pin does — the
                // automatic reconnect re-reads these options.
                options.Headers[AgentContract.VersionHeader] =
                    AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture);
            })
            // B2/T0-2: unbounded jittered backoff — the connection retries for
            // the life of the process instead of giving up after ~40 s.
            .WithAutomaticReconnect(reconnectPolicy)
            .Build();

        connection.Reconnecting += ex =>
        {
            logger.LogWarning(ex, "SignalR connection lost; reconnecting (unbounded retry)…");
            return Task.CompletedTask;
        };

        connection.Reconnected += async connectionId =>
        {
            logger.LogInformation(
                "SignalR connection re-established (connectionId={ConnectionId}).", connectionId);
            foreach (var handler in _reconnectedHandlers)
            {
                try
                {
                    await handler().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Reconnected handler failed — continuing.");
                }
            }
        };

        connection.Closed += async ex =>
        {
            if (ex is not null)
            {
                logger.LogError(ex, "SignalR connection closed with error.");
            }
            else
            {
                logger.LogInformation("SignalR connection closed cleanly.");
            }

            // Surface only a permanent close of the CURRENT connection that the
            // agent did not initiate itself — a replaced or deliberately stopped
            // connection's Closed must not trigger a supervisor restart.
            if (_deliberateStop || !ReferenceEquals(connection, _connection))
            {
                return;
            }

            foreach (var handler in _closedHandlers)
            {
                try
                {
                    await handler(ex).ConfigureAwait(false);
                }
                catch (Exception hx)
                {
                    logger.LogWarning(hx, "Closed handler failed — continuing.");
                }
            }
        };

        // Wire up server-push handlers BEFORE starting the connection so no
        // messages can arrive before the handlers are registered.
        foreach (var handler in _deploymentHandlers)
        {
            connection.On<DeploymentPlan>("RunDeploymentAsync", handler);
        }
        foreach (var handler in _adhocHandlers)
        {
            connection.On<AdhocScriptCommand>("RunAdhocScriptAsync", handler);
        }
        foreach (var handler in _cancelHandlers)
        {
            connection.On<Guid, string?>("CancelDeploymentAsync", handler);
        }

        // Publish before StartAsync: initial-start failures throw (no Closed
        // event fires for them), and the supervisor's retry re-enters here.
        _connection = connection;

        // First StartAsync brings the report pump up; it outlives individual
        // connections (buffered reports must survive a supervisor restart).
        _pumpTask ??= Task.Run(() => _outbox.PumpAsync(_pumpCts.Token), CancellationToken.None);

        await connection.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            _deliberateStop = true;
            await _connection.StopAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Agent → Server ─────────────────────────────────────────────────────

    public Task<AgentRegistrationResult> RegisterAsync(
        AgentRegistrationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _connection is not null
            ? _connection.InvokeAsync<AgentRegistrationResult>("RegisterAsync", request, ct)
            : throw new InvalidOperationException(
                "Cannot register: the hub connection has not been started.");
    }

    public Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _connection is not null
            ? _connection.InvokeAsync("HeartbeatAsync", request, ct)
            : Task.CompletedTask;
    }

    public Task ReportStatusAsync(string status, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(status);
        return _connection is not null
            ? _connection.InvokeAsync("ReportStatusAsync", status, ct)
            : Task.CompletedTask;
    }

    // B2: work-result reports go through the outbox — the call returns once the
    // report is QUEUED; a single pump delivers strictly in order with
    // at-least-once retry across disconnects (DispatchId dedups server-side).
    // Pre-B2 these were direct InvokeAsync calls that simply failed (and lost
    // the report) whenever the connection was down.

    public Task AppendLogAsync(
        Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        _outbox.Enqueue(new OutboxItem.Log(deploymentId, dispatchId, stepIndex, level, message));
        return Task.CompletedTask;
    }

    public Task CompleteDeploymentAsync(
        Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct)
    {
        _outbox.Enqueue(new OutboxItem.DeploymentCompleted(deploymentId, dispatchId, success, errorMessage));
        return Task.CompletedTask;
    }

    // F2: queued rather than sent direct so FIFO puts it ahead of this plan's own
    // step reports and completion — the server can never observe a step report for
    // an attempt it hasn't yet armed. Droppable as poison (advisory).
    public Task ReportExecutionStartedAsync(
        Guid deploymentId, Guid dispatchId, CancellationToken ct)
    {
        _outbox.Enqueue(new OutboxItem.ExecutionStarted(deploymentId, dispatchId));
        return Task.CompletedTask;
    }

    public Task ReportStepCompletedAsync(
        Guid deploymentId,
        Guid dispatchId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        IReadOnlyDictionary<string, string> outputVariables,
        IReadOnlyCollection<string> sensitiveOutputNames,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        ArgumentNullException.ThrowIfNull(outputVariables);

        // SignalR JSON serialiser handles IReadOnlyDictionary fine, but the hub
        // signature uses Dictionary<string,string> for symmetry with the typed
        // interface. Materialise once at the boundary (also snapshots the maps —
        // the outbox may deliver long after the executor mutates its locals).
        var payload = new Dictionary<string, string>(outputVariables, StringComparer.OrdinalIgnoreCase);

        // T0-6: the sensitive-name subset travels as a List<string> so the hub
        // knows which values to encrypt at rest + mask. Never null on the wire.
        var sensitive = sensitiveOutputNames?.ToList() ?? [];

        _outbox.Enqueue(new OutboxItem.StepCompleted(
            deploymentId, dispatchId, stepIndex, stepName, success, errorMessage, payload, sensitive));
        return Task.CompletedTask;
    }

    public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        _outbox.Enqueue(new OutboxItem.AdhocResult(result));
        return Task.CompletedTask;
    }

    /// <summary>The outbox pump's sender: one hub invocation per item, against
    /// the CURRENT connection (replaced across supervisor restarts).</summary>
    private Task SendOutboxItemAsync(OutboxItem item, CancellationToken ct)
    {
        var connection = _connection
            ?? throw new InvalidOperationException("Hub connection is not started.");

        return item switch
        {
            OutboxItem.Log log => connection.InvokeAsync(
                "AppendLogAsync",
                log.DeploymentId, log.DispatchId, log.StepIndex, log.Level, log.Message, ct),

            OutboxItem.StepCompleted s => connection.InvokeAsync(
                "ReportStepCompletedAsync",
                s.DeploymentId, s.DispatchId, s.StepIndex, s.StepName, s.Success,
                s.ErrorMessage, s.OutputVariables, s.SensitiveOutputNames, ct),

            OutboxItem.DeploymentCompleted d => connection.InvokeAsync(
                "CompleteDeploymentAsync", d.DeploymentId, d.DispatchId, d.Success, d.ErrorMessage, ct),

            OutboxItem.AdhocResult a => connection.InvokeAsync(
                "ReportAdhocResultAsync", a.Result, ct),

            OutboxItem.ExecutionStarted e => connection.InvokeAsync(
                "ReportExecutionStartedAsync", e.DeploymentId, e.DispatchId, ct),

            _ => throw new NotSupportedException($"Unknown outbox item {item.GetType().Name}."),
        };
    }

    // ── Server → Agent ─────────────────────────────────────────────────────

    public void OnRunDeployment(Func<DeploymentPlan, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _deploymentHandlers.Add(handler);

        // If already connected (e.g. re-wiring after reconnect), register immediately.
        _connection?.On<DeploymentPlan>("RunDeploymentAsync", handler);
    }

    public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _adhocHandlers.Add(handler);
        _connection?.On<AdhocScriptCommand>("RunAdhocScriptAsync", handler);
    }

    public void OnCancelDeployment(Func<Guid, string?, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _cancelHandlers.Add(handler);
        _connection?.On<Guid, string?>("CancelDeploymentAsync", handler);
    }

    public void OnClosed(Func<Exception?, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _closedHandlers.Add(handler);
    }

    public void OnReconnected(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _reconnectedHandlers.Add(handler);
    }

    // ── IAsyncDisposable ───────────────────────────────────────────────────

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the agent host registers this singleton under BOTH
        // SignalRServerLink and IServerLink, so the DI container disposes the
        // same instance twice — the second pass must be a no-op (the CTS
        // below is not double-dispose-safe).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _deliberateStop = true;

        await _pumpCts.CancelAsync().ConfigureAwait(false);
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected shutdown path.
            }
        }
        _pumpCts.Dispose();

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// SignalR implementation of <see cref="IServerLink"/>.
/// Token is delivered via the <c>AccessTokenProvider</c> delegate so the
/// JWT travels in the query string (<c>?access_token=…</c>) on WebSocket
/// upgrades — matching what the server's JwtBearerEvents.OnMessageReceived
/// expects.
/// </summary>
public sealed class SignalRServerLink(ILogger<SignalRServerLink> logger) : IServerLink
{
    private HubConnection? _connection;

    // Handlers registered before StartAsync; wired onto _connection in StartAsync.
    private readonly List<Func<DeploymentPlan, Task>> _deploymentHandlers = [];
    private readonly List<Func<AdhocScriptCommand, Task>> _adhocHandlers = [];

    // ── IServerLink ────────────────────────────────────────────────────────

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task StartAsync(string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentNullException.ThrowIfNull(agentJwtProvider);

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/agent";

        _connection = new HubConnectionBuilder()
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
            })
            // B2/T0-2: unbounded jittered backoff — the connection retries for
            // the life of the process instead of giving up after ~40 s.
            .WithAutomaticReconnect(new AgentReconnectPolicy(logger))
            .Build();

        _connection.Reconnecting += ex =>
        {
            logger.LogWarning(ex, "SignalR connection lost; reconnecting (unbounded retry)…");
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            logger.LogInformation(
                "SignalR connection re-established (connectionId={ConnectionId}).", connectionId);
            return Task.CompletedTask;
        };

        _connection.Closed += ex =>
        {
            if (ex is not null)
            {
                logger.LogError(ex, "SignalR connection closed with error.");
            }
            else
            {
                logger.LogInformation("SignalR connection closed cleanly.");
            }

            return Task.CompletedTask;
        };

        // Wire up server-push handlers BEFORE starting the connection so no
        // messages can arrive before the handlers are registered.
        foreach (var handler in _deploymentHandlers)
        {
            _connection.On<DeploymentPlan>("RunDeploymentAsync", handler);
        }
        foreach (var handler in _adhocHandlers)
        {
            _connection.On<AdhocScriptCommand>("RunAdhocScriptAsync", handler);
        }

        await _connection.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Agent → Server ─────────────────────────────────────────────────────

    public Task RegisterAsync(AgentRegistrationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _connection is not null
            ? _connection.InvokeAsync("RegisterAsync", request, ct)
            : Task.CompletedTask;
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

    public Task AppendLogAsync(
        Guid deploymentId, int stepIndex, string level, string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _connection is not null
            ? _connection.InvokeAsync("AppendLogAsync", deploymentId, stepIndex, level, message, ct)
            : Task.CompletedTask;
    }

    public Task CompleteDeploymentAsync(
        Guid deploymentId, bool success, string? errorMessage, CancellationToken ct)
    {
        return _connection is not null
            ? _connection.InvokeAsync("CompleteDeploymentAsync",
                deploymentId, success, errorMessage, ct)
            : Task.CompletedTask;
    }

    public Task ReportStepCompletedAsync(
        Guid deploymentId,
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
        if (_connection is null)
        {
            return Task.CompletedTask;
        }

        // SignalR JSON serialiser handles IReadOnlyDictionary fine, but the hub
        // signature uses Dictionary<string,string> for symmetry with the typed
        // interface. Materialise once at the boundary.
        var payload = outputVariables as Dictionary<string, string>
                      ?? new Dictionary<string, string>(outputVariables, StringComparer.OrdinalIgnoreCase);

        // T0-6: the sensitive-name subset travels as a List<string> so the hub
        // knows which values to encrypt at rest + mask. Never null on the wire.
        var sensitive = sensitiveOutputNames as List<string>
                        ?? (sensitiveOutputNames?.ToList() ?? []);

        return _connection.InvokeAsync(
            "ReportStepCompletedAsync",
            deploymentId, stepIndex, stepName, success, errorMessage, payload, sensitive, ct);
    }

    public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _connection is not null
            ? _connection.InvokeAsync("ReportAdhocResultAsync", result, ct)
            : Task.CompletedTask;
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

    // ── IAsyncDisposable ───────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

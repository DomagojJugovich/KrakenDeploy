using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using KrakenDeploy.Contracts;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// <see cref="IServerLink"/> for Polling transport mode — the agent polls the
/// server every <see cref="PollInterval"/> seconds for pending work. All
/// communication goes via HTTP REST; no persistent connection is required.
/// </summary>
public sealed class PollingServerLink : IServerLink
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly List<Func<DeploymentPlan, Task>> _onRunDeployment = [];

    private HttpClient? _http;
    private CancellationTokenSource? _pollCts;
    private string _serverUrl = "";
    private Guid _targetId;

    public bool IsConnected { get; private set; }

    public void OnRunDeployment(Func<DeploymentPlan, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onRunDeployment.Add(handler);
    }

    public Task StartAsync(string serverUrl, string agentJwt, CancellationToken ct)
    {
        _serverUrl = serverUrl.TrimEnd('/');

        // Extract the target ID from the agent JWT so we can poll the
        // correct pending-work endpoint.
        var tokenHandler = new JwtSecurityTokenHandler();
        var jwt = tokenHandler.ReadJwtToken(agentJwt);
        var subClaim = jwt.Claims.FirstOrDefault(
            c => c.Type == ClaimTypes.NameIdentifier);
        if (subClaim is null || !Guid.TryParse(subClaim.Value, out var targetId))
        {
            throw new InvalidOperationException(
                "Agent JWT is missing the NameIdentifier claim.");
        }

        _targetId = targetId;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentJwt);

        IsConnected = true;

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = PollLoopAsync(_pollCts.Token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        IsConnected = false;
        _pollCts?.Cancel();

        return Task.CompletedTask;
    }

    // ── Agent → Server ────────────────────────────────────────────────────────

    public async Task RegisterAsync(AgentRegistrationRequest request, CancellationToken ct)
    {
        if (_http is null)
        {
            return;
        }

        var url = $"{_serverUrl}/api/agents/register";
        await _http.PostAsJsonAsync(url, request, ct).ConfigureAwait(false);
    }

    public async Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        if (_http is null)
        {
            return;
        }

        var url = $"{_serverUrl}/api/agents/heartbeat";
        await _http.PostAsJsonAsync(url, request, ct).ConfigureAwait(false);
    }

    public async Task ReportStatusAsync(string status, CancellationToken ct)
    {
        if (_http is null)
        {
            return;
        }

        var url = $"{_serverUrl}/api/agents/status";
        var content = new StringContent(status);
        await _http.PostAsync(url, content, ct).ConfigureAwait(false);
    }

    public async Task AppendLogAsync(
        Guid deploymentId, string level, string message, CancellationToken ct)
    {
        if (_http is null)
        {
            return;
        }

        var url = $"{_serverUrl}/api/deployments/{deploymentId}/logs";
        var body = new DeploymentLogLineRequest(level, message);
        await _http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
    }

    public async Task CompleteDeploymentAsync(
        Guid deploymentId, bool success, string? errorMessage, CancellationToken ct)
    {
        if (_http is null)
        {
            return;
        }

        var url = $"{_serverUrl}/api/deployments/{deploymentId}/complete";
        var body = new CompleteDeploymentRequest(success, errorMessage);
        await _http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);
    }

    public Task ReportStepCompletedAsync(
        Guid deploymentId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        IReadOnlyDictionary<string, string> outputVariables,
        CancellationToken ct)
    {
        // TODO(polling-transport): expose a REST endpoint so this transport can
        // forward the M14.4 per-step boundary (outcome + outputs + per-step
        // Required attribution). SignalR is the primary path today and already
        // handles this. Until then, output variables and per-step outcomes are
        // not reported to the server when an agent runs over the Polling
        // transport — subsequent steps still see prior outputs in their own
        // run via the agent-local outputsByStep accumulator, and the server
        // sees deployment-level success/failure via CompleteDeploymentAsync.
        _ = deploymentId; _ = stepIndex; _ = stepName; _ = success;
        _ = errorMessage; _ = outputVariables; _ = ct;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _http?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Poll loop ──────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                if (_http is null)
                {
                    continue;
                }

                var response = await _http.GetAsync(
                    $"{_serverUrl}/api/agents/pending-work/{_targetId}",
                    ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = await response.Content
                    .ReadFromJsonAsync<JsonDocument>(ct)
                    .ConfigureAwait(false);

                if (doc is null)
                {
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("pending", out var pendingProp)
                    || !pendingProp.GetBoolean())
                {
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("plan", out var planProp))
                {
                    continue;
                }

                var plan = planProp.Deserialize<DeploymentPlan>();
                if (plan is null)
                {
                    continue;
                }

                foreach (var handler in _onRunDeployment)
                {
                    await handler(plan).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Silently retry on next poll interval.
            }
        }
    }
}

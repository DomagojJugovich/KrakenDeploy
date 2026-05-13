using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KrakenDeploy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// <see cref="IServerLink"/> for Direct transport mode — the agent hosts an
/// HTTP listener that the server pushes deployment plans to. Agent-to-server
/// calls (heartbeat, logs, completion) go via plain HTTP REST.
/// </summary>
public sealed class DirectServerLink : IServerLink
{
    private readonly List<Func<DeploymentPlan, Task>> _onRunDeployment = [];

    private IWebHost? _listener;
    private HttpClient? _http;
    private string _serverUrl = "";

    public bool IsConnected { get; private set; }

    public void OnRunDeployment(Func<DeploymentPlan, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _onRunDeployment.Add(handler);
    }

    public async Task StartAsync(string serverUrl, string agentJwt, CancellationToken ct)
    {
        _serverUrl = serverUrl.TrimEnd('/');

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentJwt);

        // Start a minimal Kestrel listener. Port is hard-coded for now
        // (configurable in a later pass).
        var handler = new PipelineHandler(_onRunDeployment);

        var host = new WebHostBuilder()
            .UseKestrel(options =>
            {
                options.Listen(IPAddress.Any, 10933);
            })
            .Configure(app =>
            {
                app.Run(handler.HandleAsync);
            })
            .Build();

        _listener = host;
        IsConnected = true;
        await host.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        IsConnected = false;

        if (_listener is not null)
        {
            await _listener.StopAsync(ct).ConfigureAwait(false);
            _listener.Dispose();
            _listener = null;
        }
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

    public Task ReportStepOutputVariablesAsync(
        Guid deploymentId, string stepName,
        IReadOnlyDictionary<string, string> outputVariables, CancellationToken ct)
    {
        // TODO(direct-transport): expose a REST endpoint so this transport can
        // forward Set-OctopusVariable captures. SignalR is the primary path
        // today and already handles this. Until then, output variables are not
        // reported to the server when an agent runs over the Direct transport,
        // but they are still merged into subsequent steps within the same run.
        _ = deploymentId; _ = stepName; _ = outputVariables; _ = ct;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _listener.Dispose();
        }

        _http?.Dispose();
    }

    /// <summary>
    /// Minimal ASP.NET Core middleware that deserializes a <see cref="DeploymentPlan"/>
    /// from the request body and fires all registered handlers.
    /// </summary>
    private sealed class PipelineHandler(List<Func<DeploymentPlan, Task>> handlers)
    {
        public async Task HandleAsync(HttpContext context)
        {
            if (!context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                || !context.Request.Path.Equals("/api/agent/deploy", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 404;
                return;
            }

            try
            {
                var plan = await context.Request.ReadFromJsonAsync<DeploymentPlan>()
                    .ConfigureAwait(false);
                if (plan is null)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                foreach (var handler in handlers)
                {
                    await handler(plan).ConfigureAwait(false);
                }

                context.Response.StatusCode = 200;
            }
            catch
            {
                context.Response.StatusCode = 500;
            }
        }
    }
}

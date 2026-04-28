using KrakenDeploy.Contracts;
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

    // ── IServerLink ────────────────────────────────────────────────────────

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task StartAsync(string serverUrl, string agentJwt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentException.ThrowIfNullOrEmpty(agentJwt);

        var hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/agent";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                // Deliver JWT via query string so it survives the WebSocket upgrade.
                options.AccessTokenProvider = () => Task.FromResult<string?>(agentJwt);
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.Reconnecting += ex =>
        {
            logger.LogWarning(ex, "SignalR connection lost; attempting to reconnect…");
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

        await _connection.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            await _connection.StopAsync(ct).ConfigureAwait(false);
        }
    }

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

    // ── IAsyncDisposable ───────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

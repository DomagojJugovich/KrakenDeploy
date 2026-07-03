using System.Net;
using System.Net.Http.Json;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// First hosted service to run. Loads an existing identity from <c>agent.json</c>,
/// or exchanges the one-time registration token for a long-lived JWT and persists it.
/// Signals <see cref="AgentContext.IdentityReady"/> on success; calls
/// <see cref="IHostApplicationLifetime.StopApplication"/> on unrecoverable failure.
/// </summary>
public sealed class RegistrationHostedService(
    AgentContext context,
    AgentIdentityStore identityStore,
    IOptions<ServerOptions> serverOptions,
    IHostApplicationLifetime lifetime,
    ILogger<RegistrationHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── 1. Try existing identity ─────────────────────────────────────
        var identity = await identityStore
            .TryLoadAsync(stoppingToken)
            .ConfigureAwait(false);

        if (identity is not null)
        {
            logger.LogInformation(
                "Agent identity loaded from store. AgentId={AgentId}", identity.AgentId);
            context.SetIdentity(identity, identity.TransportMode);
            return;
        }

        // ── 2. Need registration — check config ──────────────────────────
        var opts = serverOptions.Value;

        if (string.IsNullOrWhiteSpace(opts.Url))
        {
            logger.LogError(
                "Server:Url is not configured. Set it in appsettings.json or via the " +
                "--Server:Url command-line argument and restart.");
            lifetime.StopApplication();
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.RegistrationToken))
        {
            logger.LogError(
                "No agent identity found and Server:RegistrationToken is not set. " +
                "Generate a token in the KrakenDeploy Targets UI, then restart with " +
                "--Server:RegistrationToken=<token> or set it in appsettings.json.");
            lifetime.StopApplication();
            return;
        }

        // ── 3. Register with retry / back-off ────────────────────────────
        logger.LogInformation(
            "Registering agent with server {ServerUrl}.", opts.Url);

        identity = await RegisterWithRetryAsync(opts, stoppingToken).ConfigureAwait(false);

        if (identity is null)
        {
            logger.LogError(
                "Agent registration failed. The token may be expired, already used, " +
                "or the server is unreachable. Generate a new token and restart.");
            lifetime.StopApplication();
            return;
        }

        await identityStore.SaveAsync(identity, stoppingToken).ConfigureAwait(false);

        logger.LogInformation(
            "Agent registered successfully. AgentId={AgentId}", identity.AgentId);

        context.SetIdentity(identity, identity.TransportMode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<AgentIdentity?> RegisterWithRetryAsync(
        ServerOptions opts,
        CancellationToken ct)
    {
        const int MaxAttempts = 5;
        var delay = TimeSpan.FromSeconds(5);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var http = new HttpClient();
                var url = $"{opts.Url.TrimEnd('/')}/api/agents/register";
                var body = new RegisterAgentRequest(opts.RegistrationToken!);

                using var response = await http
                    .PostAsJsonAsync(url, body, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Token invalid/consumed — retrying won't help.
                    logger.LogError(
                        "Server rejected the registration token (HTTP 401). " +
                        "The token is invalid, expired, or already used.");
                    return null;
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content
                    .ReadFromJsonAsync<RegisterAgentResponse>(ct)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    return null;
                }

                // Blue-green version pin: behind the per-node slot router the
                // registration response carries X-KD-Release (the release that
                // served this request). Persist it so the hub connection echoes
                // it and stays on that release across reconnects. Absent when no
                // router is in play (single-instance) — stays null.
                string? releaseId = null;
                if (response.Headers.TryGetValues("X-KD-Release", out var releaseHeader))
                {
                    releaseId = releaseHeader.FirstOrDefault();
                }

                return new AgentIdentity
                {
                    AgentId = result.AgentId,
                    AgentToken = result.AgentJwt,
                    ServerUrl = opts.Url,
                    TransportMode = result.TransportMode,
                    ReleaseId = releaseId,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Registration attempt {Attempt}/{Max} failed. Retrying in {Delay}s.",
                    attempt, MaxAttempts, (int)delay.TotalSeconds);

                if (attempt < MaxAttempts)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
                }
            }
        }

        return null;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// A8 sliding token refresh. Once the agent's bearer token is past half of its
/// validity window, exchanges it for a fresh one via
/// <c>POST /api/agents/refresh-token</c> (authenticated with the CURRENT token —
/// no one-time registration token involved), persists the new identity to
/// <c>agent.json</c>, and swaps it into <see cref="AgentContext"/> so the SignalR
/// and gRPC token providers pick it up lazily. With no rotation server-side, the
/// old token stays valid until its own expiry, so the swap has no failure window.
/// <para>
/// A revoked token gets 401 here (the refresh endpoint runs the same revocation
/// check as every other call) — revocation therefore cannot be outrun by
/// refreshing. An agent offline longer than the token lifetime cannot refresh at
/// all and must be re-enrolled by an operator.
/// </para>
/// </summary>
public sealed class TokenRefreshHostedService(
    AgentContext context,
    AgentIdentityStore identityStore,
    TimeProvider timeProvider,
    ILogger<TokenRefreshHostedService> logger)
    : BackgroundService
{
    // How often the token's remaining lifetime is inspected. Checks are trivial
    // (a local timestamp comparison), so the only constraint is being much
    // smaller than half the token lifetime (45 d at the default 90 d).
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await context.IdentityReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Offline-drop mode has no live transport and no bearer token to renew.
        if (string.IsNullOrEmpty(context.Identity?.AgentToken))
        {
            logger.LogDebug("No agent bearer token present; token refresh service idle.");
            return;
        }

        // First check immediately (an agent booting after weeks offline may be
        // deep past half-life), then on the periodic cadence.
        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        do
        {
            try
            {
                await CheckAndRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Transient (server unreachable, timeout) — the next tick retries;
                // half-life leaves ~45 d of retry budget at default settings.
                logger.LogWarning(ex, "Token refresh attempt failed; will retry.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task CheckAndRefreshAsync(CancellationToken ct)
    {
        var identity = context.Identity;
        if (identity is null || string.IsNullOrEmpty(identity.AgentToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (AgentTokenRefreshPolicy.TryGetValidityWindow(identity.AgentToken, out var nbf, out var exp))
        {
            if (!AgentTokenRefreshPolicy.ShouldRefresh(now, nbf, exp))
            {
                return;
            }

            if (now >= exp)
            {
                // The refresh call below will 401; make the operator action obvious.
                logger.LogError(
                    "Agent bearer token expired {Expired} — the server will refuse a refresh. " +
                    "Re-enroll the agent with a new registration token.", exp);
            }
        }
        else
        {
            // Unreadable window (unexpected for our own token) — refresh eagerly
            // rather than never; the server is the authority on validity anyway.
            logger.LogWarning("Could not read the bearer token's validity window; attempting refresh.");
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.AgentToken);

        var url = $"{identity.ServerUrl.TrimEnd('/')}/api/agents/refresh-token";
        using var response = await http.PostAsync(url, content: null, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            logger.LogError(
                "Token refresh rejected ({Status}) — the token has been revoked or has expired. " +
                "An operator must re-enroll this agent with a new registration token.",
                (int)response.StatusCode);
            return;
        }

        response.EnsureSuccessStatusCode();

        var refreshed = await response.Content
            .ReadFromJsonAsync<RefreshAgentTokenResponse>(ct)
            .ConfigureAwait(false);
        if (refreshed is null || string.IsNullOrEmpty(refreshed.AgentJwt))
        {
            logger.LogWarning("Token refresh returned an empty payload; keeping the current token.");
            return;
        }

        // Persist FIRST, then swap the in-memory identity: if the process dies
        // between the two, agent.json already holds the new token and the old one
        // is still valid (no rotation) — no state is lost either way.
        var renewed = new AgentIdentity
        {
            AgentId       = identity.AgentId,
            AgentToken    = refreshed.AgentJwt,
            ServerUrl     = identity.ServerUrl,
            TransportMode = identity.TransportMode,
            ReleaseId     = identity.ReleaseId,
        };
        await identityStore.SaveAsync(renewed, ct).ConfigureAwait(false);
        context.SetIdentity(renewed, context.TransportMode);

        logger.LogInformation("Agent bearer token refreshed (sliding renewal).");
    }
}

using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// B2/T0-2 — unbounded reconnect pacing for the agent's hub connection (also
/// consulted by <c>ServerLinkHostedService</c> for its initial-connect and
/// supervisor restarts, so every path paces identically). The agent must keep
/// trying for the life of the process: a server restart, deploy, or blue-green
/// slot swap longer than the default SignalR retry window (~40 s) must never
/// strand the agent offline until an operator restarts its process.
/// <para>
/// Pacing: attempt 0 retries immediately (rides out sub-second blips), then
/// full-jitter exponential backoff — <c>random(0, min(cap, base·2^(n−1)))</c> —
/// capped at 30 s, forever. Full jitter spreads a fleet's reconnect storm
/// after a server restart instead of synchronising it.
/// </para>
/// <para>
/// Auth lane: a 401/403 while reconnecting means the token was revoked
/// (A8 <c>atv</c>) or expired past its refresh budget. Retrying at blip pace
/// would hammer the server with doomed negotiates, so the policy switches to
/// a fixed 5-minute cadence and logs that operator re-enrollment is required.
/// It still never returns <c>null</c> (give up): the slow lane costs nothing
/// and self-heals if the credential situation is repaired in place.
/// </para>
/// </summary>
public sealed class AgentReconnectPolicy(
    ILogger logger,
    Func<double>? jitterSource = null) : IRetryPolicy
{
    /// <summary>Backoff ceiling for the first backed-off attempt.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling the jittered exponential backoff saturates at.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Fixed cadence while reconnect attempts fail with 401/403.</summary>
    public static readonly TimeSpan AuthFailureDelay = TimeSpan.FromMinutes(5);

    private readonly Func<double> _jitter = jitterSource ?? Random.Shared.NextDouble;
    private bool _inAuthFailureStreak;

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);

        if (IsAuthFailure(retryContext.RetryReason))
        {
            // Log once per streak, not once per 5-minute attempt.
            if (!_inAuthFailureStreak)
            {
                _inAuthFailureStreak = true;
                logger.LogError(
                    "Server rejected the agent token (401/403) while reconnecting. The token " +
                    "has been revoked or has expired — an operator must re-enroll this agent. " +
                    "Retrying every {Delay} in case the credential is restored.",
                    AuthFailureDelay);
            }
            return AuthFailureDelay;
        }

        _inAuthFailureStreak = false;

        if (retryContext.PreviousRetryCount == 0)
        {
            return TimeSpan.Zero;
        }

        // Exponent clamp keeps Math.Pow finite; anything ≥ 5 doublings already
        // saturates the 30 s cap.
        var exponent = Math.Min(retryContext.PreviousRetryCount - 1, 30);
        var ceilingSeconds = Math.Min(
            MaxDelay.TotalSeconds,
            BaseDelay.TotalSeconds * Math.Pow(2, exponent));

        return TimeSpan.FromSeconds(ceilingSeconds * _jitter());
    }

    private static bool IsAuthFailure(Exception? reason)
        => reason is HttpRequestException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
        };
}

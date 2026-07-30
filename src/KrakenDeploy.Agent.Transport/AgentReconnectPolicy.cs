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
/// <para>
/// <b>Churn lane.</b> <see cref="RetryContext.PreviousRetryCount"/> restarts at zero for
/// every reconnect EPISODE, and attempt zero is deliberately immediate — which together
/// mean a connection that keeps dying moments after it is established never backs off at
/// all. That is not hypothetical: the hub aborts a connection whose target has been
/// deleted or retired, and <c>Context.Abort()</c> drops the transport rather than closing
/// it, so the client treats each abort as a fresh blip and reconnects at round-trip
/// cadence, forever, against a server that will refuse it every time. So the policy also
/// counts CONSECUTIVE SHORT-LIVED connections, via
/// <see cref="NoteConnected"/>/<see cref="NoteConnectionLost"/>, and paces on whichever
/// count is higher. One long-lived connection clears it.
/// </para>
/// </summary>
public sealed class AgentReconnectPolicy(
    ILogger logger,
    Func<double>? jitterSource = null,
    TimeProvider? timeProvider = null) : IRetryPolicy
{
    /// <summary>Backoff ceiling for the first backed-off attempt.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling the jittered exponential backoff saturates at.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Fixed cadence while reconnect attempts fail with 401/403.</summary>
    public static readonly TimeSpan AuthFailureDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A connection that lived at least this long counts as having been genuinely useful,
    /// and clears the churn counter. Comfortably longer than the connect→abort path, which
    /// completes in milliseconds, and comfortably shorter than any real working session.
    /// </summary>
    public static readonly TimeSpan MinUsefulConnection = TimeSpan.FromSeconds(30);

    private readonly Func<double> _jitter = jitterSource ?? Random.Shared.NextDouble;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private bool _inAuthFailureStreak;

    private long _churn;
    private DateTimeOffset? _connectedAt;

    /// <summary>
    /// Records that a connection is now established. Called on the initial connect and on
    /// every successful reconnect.
    /// </summary>
    public void NoteConnected() => _connectedAt = _clock.GetUtcNow();

    /// <summary>
    /// Records that the established connection has dropped. A connection that did not
    /// survive <see cref="MinUsefulConnection"/> counts toward the churn lane; a longer one
    /// resets it, because the link demonstrably worked.
    /// </summary>
    public void NoteConnectionLost()
    {
        var lifetime = _connectedAt is { } since ? _clock.GetUtcNow() - since : TimeSpan.Zero;
        _connectedAt = null;

        if (lifetime < MinUsefulConnection)
        {
            _churn++;
        }
        else
        {
            _churn = 0;
        }
    }

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

        // Pace on whichever count is higher: the attempts within THIS episode, or the run of
        // connections that died before they were useful. Without the second term a repeating
        // server-side abort is invisible to this method — every abort starts a new episode at
        // attempt zero, and attempt zero is immediate by design.
        var attempt = Math.Max(retryContext.PreviousRetryCount, _churn);

        if (attempt == 0)
        {
            return TimeSpan.Zero;
        }

        // Exponent clamp keeps Math.Pow finite; anything ≥ 5 doublings already
        // saturates the 30 s cap.
        var exponent = Math.Min(attempt - 1, 30);
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

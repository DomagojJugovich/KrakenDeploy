using System.Net;
using KrakenDeploy.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// B2/T0-2 — unbounded reconnect pacing for the agent's hub connection (also consulted by
/// <c>ServerLinkHostedService</c> for its initial-connect and supervisor restarts, so every
/// path paces identically). The agent must keep trying for the life of the process: a server
/// restart, deploy, or blue-green slot swap longer than the default SignalR retry window
/// (~40 s) must never strand the agent offline until an operator restarts its process.
/// <para>
/// Pacing: attempt 0 retries immediately (rides out sub-second blips), then full-jitter
/// exponential backoff — <c>random(0, min(cap, base·2^(n−1)))</c> — capped at 30 s, forever.
/// Full jitter spreads a fleet's reconnect storm after a server restart instead of
/// synchronising it.
/// </para>
/// <para>
/// Slow lane: a handshake that fails with a status only an operator (or a self-upgrade) can
/// change must not be retried at blip pace, or the fleet hammers the server with doomed
/// negotiates. Two causes qualify, and they are kept DISTINCT because the operator action
/// differs — 401/403 means "re-enroll this agent", 426 means "upgrade the agent binary", and
/// sending an operator to the wrong one costs an outage. Both use the same fixed 5-minute
/// cadence and neither ever returns <c>null</c> (give up): the slow lane costs nothing and
/// self-heals if the situation is repaired in place.
/// </para>
/// <para>
/// <b>This class paces one thing only: retries of an ESTABLISHED connection that dropped at
/// the TRANSPORT level, plus the supervisor's synthesised initial-connect attempts.</b> It
/// deliberately does NOT try to pace a server-side REJECTION, and the reason is measured
/// rather than argued. An earlier revision carried a "churn lane" here — a count of
/// consecutive short-lived connections — on the premise that the hub aborting a connection
/// (deleted or retired target) looked to the client like a fresh blip and reconnected at
/// round-trip cadence forever. Executed against a real hub
/// (<c>ReconnectE2ETests.A_server_side_rejection_fires_Closed_and_never_reconnects</c>),
/// every part of that premise is false:
/// <list type="bullet">
///   <item>a throw or <c>Context.Abort()</c> inside <c>OnConnectedAsync</c> lets
///     <c>StartAsync</c> SUCCEED, then fires <c>Closed</c>;</item>
///   <item><c>Reconnecting</c> never fires and <see cref="NextRetryDelay"/> is never called,
///     so a counter fed from the <c>Reconnecting</c> event could not observe the failure it
///     was written for;</item>
///   <item>and for a drop it CAN observe, <c>HubConnection</c> computes the delay BEFORE
///     raising <c>Reconnecting</c>, so the counter lagged the delay it was meant to pace by a
///     whole episode.</item>
/// </list>
/// A server-side rejection therefore surfaces as a permanent close, and pacing it belongs in
/// the only loop that observes one — the supervisor's, which is where it now lives.
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

    /// <summary>
    /// Fixed cadence while the handshake fails for a reason only an operator or a self-upgrade
    /// can change (401/403, or 426 from the wire-contract gate).
    /// </summary>
    public static readonly TimeSpan OperatorActionDelay = TimeSpan.FromMinutes(5);

    private readonly Func<double> _jitter = jitterSource ?? Random.Shared.NextDouble;
    private SlowLaneCause _slowLane;

    /// <summary>
    /// Why the handshake is being refused in a way retrying cannot fix. Kept as an enum rather
    /// than a bool so the log line names the right remedy: these two are the classic pair to
    /// confuse, and the wire-contract refusal was reaching the operator as "re-enroll this
    /// agent" while its real fix was a binary upgrade.
    /// </summary>
    private enum SlowLaneCause
    {
        None,

        /// <summary>401/403 — the token was revoked (A8 <c>atv</c>) or expired past its
        /// refresh budget.</summary>
        Credential,

        /// <summary>426 — this agent's wire contract does not match the server's.</summary>
        Contract,
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);

        var cause = Classify(retryContext.RetryReason);
        if (cause != SlowLaneCause.None)
        {
            // Log once per streak, not once per 5-minute attempt — but DO re-log when the
            // cause changes, because that transition is the operator's signal that their fix
            // took effect (or that a second problem is now in front of the first).
            if (_slowLane != cause)
            {
                _slowLane = cause;
                LogSlowLane(cause);
            }
            return OperatorActionDelay;
        }

        _slowLane = SlowLaneCause.None;

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

    /// <summary>
    /// Whether a handshake failure is one retrying cannot fix, and which kind.
    /// <para>
    /// The 426 arm is what makes the wire-contract refusal diagnosable at all on the agent
    /// side. <c>HttpConnection.NegotiateAsync</c> calls <c>EnsureSuccessStatusCode()</c> before
    /// touching the response, so the gate's body AND its
    /// <c>X-KD-Contract-Server</c> header are both discarded — the agent's exception message is
    /// only "Response status code does not indicate success: 426 (Upgrade Required)." The
    /// status code survives on <see cref="HttpRequestException.StatusCode"/>, and that is
    /// enough: the cause is unambiguous, and the number the operator needs is on the server's
    /// log line. Verified by executing a 426 negotiate against a real client, because the
    /// previous revision assumed the header was readable and shipped a diagnostic nobody
    /// could see.
    /// </para>
    /// </summary>
    private static SlowLaneCause Classify(Exception? reason)
        => reason is HttpRequestException http
            ? http.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    => SlowLaneCause.Credential,
                HttpStatusCode.UpgradeRequired
                    => SlowLaneCause.Contract,
                _ => SlowLaneCause.None,
            }
            : SlowLaneCause.None;

    private void LogSlowLane(SlowLaneCause cause)
    {
        if (cause == SlowLaneCause.Credential)
        {
            logger.LogError(
                "Server rejected the agent token (401/403) while connecting. The token has " +
                "been revoked or has expired — an operator must RE-ENROLL this agent. " +
                "Retrying every {Delay} in case the credential is restored.",
                OperatorActionDelay);
            return;
        }

        logger.LogError(
            "Server refused this agent's wire contract with 426 Upgrade Required. This agent " +
            "speaks v{AgentContract}; the server requires a different version and the failed " +
            "negotiate does not carry which — see the SERVER log, which names both. The remedy " +
            "is an AGENT BINARY UPGRADE, not re-enrollment. The self-upgrade check runs over " +
            "REST and does not need this connection, so an agent with auto-update enabled and " +
            "a compatible build on offer will heal itself. Retrying every {Delay}.",
            AgentContract.CurrentVersion, OperatorActionDelay);
    }
}

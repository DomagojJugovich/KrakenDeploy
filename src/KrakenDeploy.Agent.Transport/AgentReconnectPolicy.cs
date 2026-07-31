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
/// <param name="onContractRefused">
/// Raised with <c>true</c> the first time a reconnect attempt is refused with 426, and with
/// <c>false</c> once one is not. This is the ONLY place a 426 met during AUTOMATIC RECONNECT is
/// observable: this policy never returns <c>null</c>, so <c>HubConnection</c> retries forever,
/// never raises <c>Closed</c>, and the supervisor stays parked — so its <c>StartAsync</c> catch,
/// which is where the agent used to learn about a refusal, is never re-entered. Without this
/// callback the self-upgrade escape hatch stayed shut on the path a server upgrade actually
/// takes: every agent's transport drops on the restart, the client's own loop gets the 426, and
/// nothing told the updater it was allowed to swap.
/// </param>
public sealed class AgentReconnectPolicy(
    ILogger logger,
    Func<double>? jitterSource = null,
    TimeProvider? timeProvider = null,
    Action<bool>? onContractRefused = null) : IRetryPolicy
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

    /// <summary>
    /// A connection that lived at least this long counts as genuinely useful and resets the
    /// episode count. Comfortably longer than a connect→drop flap and comfortably shorter than
    /// any real working session.
    /// </summary>
    public static readonly TimeSpan MinUsefulConnection = TimeSpan.FromSeconds(30);

    private readonly Func<double> _jitter = jitterSource ?? Random.Shared.NextDouble;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private SlowLaneCause _slowLane;

    // Consecutive reconnect EPISODES that began without a useful connection in between. Paces
    // the flap that PreviousRetryCount cannot see: that counter restarts at 0 for every episode
    // and attempt 0 is deliberately immediate, so a link that establishes and drops repeatedly
    // reconnects at round-trip cadence forever on PreviousRetryCount alone.
    //
    // An earlier revision counted this from the Reconnecting EVENT and was doubly wrong:
    // HubConnection computes the delay BEFORE raising Reconnecting, so the counter lagged a
    // whole episode, and the event does not fire at all for a server-side rejection (which
    // arrives as Closed). The correctly-ordered signal is right here — PreviousRetryCount == 0
    // inside this method IS "a new episode just started", synchronously, on the thread that is
    // about to use the answer.
    private long _episodes;

    // Monotonic stamp of the last successful connect; 0 means "not currently connected".
    // A plain long behind Volatile/Interlocked rather than a DateTimeOffset? — the previous
    // shape was a nullable DateTimeOffset written from the SignalR thread and read from the
    // reconnect thread, which is wider than a word, so a reader could observe hasValue == true
    // over a zeroed date and compute a two-millennia lifetime. And GetTimestamp, never
    // GetUtcNow: a w32tm step on a domain-joined host must not disarm the pacing.
    private long _connectedAt;

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

    /// <summary>
    /// Records that a connection is established. Called on the initial connect and on every
    /// successful reconnect; a connection that then survives
    /// <see cref="MinUsefulConnection"/> clears the episode count.
    /// </summary>
    public void NoteConnected() => Volatile.Write(ref _connectedAt, _clock.GetTimestamp());

    /// <summary>
    /// Raises <c>onContractRefused</c> only when the refused STATE actually changes, so a
    /// consumer sees transitions rather than one notification per retry — and a 401 streak,
    /// which shares the delay lane but not the remedy, does not re-assert "not refused" on
    /// every attempt.
    /// </summary>
    private void ReportContractRefused(bool refused)
    {
        if (_reportedContractRefused == refused)
        {
            return;
        }
        _reportedContractRefused = refused;
        onContractRefused?.Invoke(refused);
    }

    private bool _reportedContractRefused;

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
            // Only the contract cause opens the updater's escape hatch. A credential failure
            // needs re-enrollment, which a binary swap cannot supply.
            ReportContractRefused(cause == SlowLaneCause.Contract);
            return OperatorActionDelay;
        }

        _slowLane = SlowLaneCause.None;
        // This attempt failed for an ordinary transport reason, so the contract is not the
        // problem and the hatch closes again.
        ReportContractRefused(false);

        // A new EPISODE has just started. Count it, unless the connection it replaced was
        // genuinely useful — in which case this is a fresh blip and the count restarts at one.
        if (retryContext.PreviousRetryCount == 0)
        {
            var stamp = Volatile.Read(ref _connectedAt);
            var lived = stamp == 0 ? TimeSpan.Zero : _clock.GetElapsedTime(stamp);
            _episodes = lived >= MinUsefulConnection ? 1 : _episodes + 1;
            Volatile.Write(ref _connectedAt, 0);
        }

        // Pace on whichever is higher: the attempts within THIS episode, or the run of episodes
        // that never produced a useful connection. Episode 1 still gets attempt 0's immediate
        // retry, so a healthy link that drops once is not penalised.
        var attempt = Math.Max(retryContext.PreviousRetryCount, _episodes - 1);
        if (attempt <= 0)
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

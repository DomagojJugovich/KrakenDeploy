using System.Net;
using KrakenDeploy.Agent.Adhoc;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Opens and SUPERVISES the SignalR control-plane connection to the server.
/// Waits for <see cref="AgentContext.IdentityReady"/> before connecting so it
/// always has a valid bearer token.
/// <para>
/// B2/T0-2 — the link must survive for the life of the process:
/// <list type="bullet">
/// <item>Initial connect retries with the same unbounded jittered backoff the
/// connection's own retry policy uses (an agent booting while the server is
/// down comes online by itself once the server does).</item>
/// <item>Transient drops are handled INSIDE the connection by
/// <see cref="AgentReconnectPolicy"/> (unbounded); they never reach this loop.</item>
/// <item>A permanent close (anything automatic reconnect does not cover) is
/// surfaced via <see cref="IServerLink.OnClosed"/> and restarts the whole
/// connect cycle — the service never idles with a dead connection.</item>
/// <item>Registration is (re-)sent after every connect and reconnect; the hub
/// re-marks the target Online in its own OnConnectedAsync either way.</item>
/// </list>
/// Clean shutdown is distinguished from failure: on <paramref name="stoppingToken"/>
/// the loop exits, reports <c>ShuttingDown</c> and stops the link deliberately.
/// </para>
/// </summary>
public sealed class ServerLinkHostedService(
    AgentContext context,
    IServerLink serverLink,
    DeploymentExecutor deploymentExecutor,
    AdhocScriptExecutor adhocExecutor,
    MachineInfoCollector machineCollector,
    IOptions<ServerOptions> serverOptions,
    IOptions<AgentConfig> agentConfig,
    TimeProvider timeProvider,
    ILogger<ServerLinkHostedService> logger)
    : BackgroundService
{
    // Signals the current supervision cycle that the link closed permanently.
    // Replaced at the start of every cycle; the OnClosed handler resolves it.
    private volatile TaskCompletionSource<Exception?>? _closedSignal;

    /// <summary>
    /// F2-followup 1 — work started by a detached push handler. Tracked for two
    /// reasons: a faulted handler must be LOGGED rather than left as an unobserved
    /// task exception (the pre-detach shape got that for free, because SignalR
    /// awaited and logged it), and shutdown gets a bounded chance to let
    /// queued-but-unstarted work unwind.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _detachedHandlers = new();

    /// <summary>How long shutdown waits for detached handlers. Deliberately short:
    /// a genuinely executing deployment can run for hours and agent death mid-deploy
    /// is B1's lease/reconciler story, not this service's. The wait exists so a plan
    /// still QUEUED on the machine gate — whose wait observes
    /// <c>stoppingToken</c> — can unwind its registry entry and staging first.</summary>
    private static readonly TimeSpan DetachedHandlerDrainTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // E8 — clear any staging trees a previous process left behind before any
        // work can arrive. Nothing is executing yet (the deployment handler is
        // wired below, after this), so the whole staging root is orphan garbage.
        deploymentExecutor.SweepOrphanedStagingOnBoot();

        // ── Wait for registration to complete ────────────────────────────
        try
        {
            await context.IdentityReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var serverUrl = serverOptions.Value.Url;

        // ── One-time handler wiring (re-applied to every connection) ─────
        // Register deployment handler BEFORE opening the connection so no
        // RunDeploymentAsync messages can arrive before the handler is wired.
        //
        // F2-followup 1 — these handlers are DETACHED deliberately, and returning
        // Task.CompletedTask is the whole point. The SignalR client dispatches
        // server→client invocations through a single-reader channel and AWAITS each
        // handler, so returning the work task (what `Task.Run(…, ct)` does — it
        // UNWRAPS) made the agent process exactly one push at a time. Measured
        // consequences of that shape:
        //   * two deployments to one box could never overlap, so B7's machine queue
        //     and F2's per-target AllowParallelTaskExecution were both unreachable —
        //     the transport, not the gate, was doing the serializing;
        //   * an ad-hoc command was not delivered while a deployment ran, so the
        //     ad-hoc gate wait / bounded refusal never fired;
        //   * a CancelDeploymentAsync push queued behind the very deployment it
        //     targeted and arrived after the run had finished, so B6's cooperative
        //     abort and process-tree kill never fired on an operator cancel;
        //   * a supervisor reconnect blocked on the in-flight run.
        // Task.Run still hops off the message-loop thread — ExecuteAsync's
        // synchronous prefix can await a superseded attempt's unwind, which must not
        // stall the loop either.
        serverLink.OnRunDeployment(plan =>
        {
            TrackDetachedHandler(
                Task.Run(
                    () => deploymentExecutor.ExecuteAsync(
                        plan, orchestrateSteps: false, hostStopping: stoppingToken),
                    stoppingToken),
                $"deployment {plan.DeploymentId} attempt {plan.DispatchId}");
            return Task.CompletedTask;
        });

        // M11.E.7 — same gate-before-open contract for ad-hoc commands.
        // The executor is fail-closed: refuses on signature mismatch /
        // missing public key, always reports back to the dispatcher.
        serverLink.OnRunAdhocScript(cmd =>
        {
            TrackDetachedHandler(
                Task.Run(() => adhocExecutor.HandleAsync(cmd, stoppingToken), stoppingToken),
                $"adhoc session {cmd.SessionId} iter {cmd.IterNumber}");
            return Task.CompletedTask;
        });

        // B6 — cooperative abort push. Synchronous signal (no Task.Run): it
        // only flips the in-flight run's CancellationTokenSource; the heavy
        // lifting (process-tree kill, failed completion) happens on the
        // executor's own flow. Unknown task id = best-effort no-op.
        serverLink.OnCancelDeployment((taskId, reason) =>
        {
            var cancelled = deploymentExecutor.TryCancel(taskId, reason);
            if (cancelled)
            {
                logger.LogInformation(
                    "Server requested cancellation of task {TaskId}: {Reason}",
                    taskId, reason ?? "no reason given");
            }
            else
            {
                logger.LogInformation(
                    "Server requested cancellation of task {TaskId}, but it is not in flight; ignored.",
                    taskId);
            }
            return Task.CompletedTask;
        });

        serverLink.OnClosed(ex =>
        {
            _closedSignal?.TrySetResult(ex);
            return Task.CompletedTask;
        });

        // The reconnect-path half of the self-upgrade escape hatch. The StartAsync catch below
        // covers an INITIAL connect refused with 426; this covers the far more common case — a
        // server upgrade drops every established connection, so the client's own automatic
        // reconnect meets the 426 instead. That path never raises Closed (the policy never gives
        // up), so the supervision loop stays parked here and StartAsync is never re-entered.
        // Without this the hatch stayed shut exactly when a contract bump needed it.
        serverLink.OnContractRefused(context.SetContractRefused);

        serverLink.OnReconnected(async () =>
        {
            logger.LogInformation(
                "Reconnected to server {ServerUrl}; re-sending registration.", serverUrl);
            var outcome = await TrySendRegistrationAsync(stoppingToken).ConfigureAwait(false);
            if (outcome == RegistrationOutcome.Refused)
            {
                // B6: refusal after an automatic reconnect (e.g. the server was
                // upgraded mid-connection). Stop the link so the supervision loop
                // re-enters its connect/refusal cycle and paces on the slow lane.
                try
                {
                    await serverLink.StopAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex,
                        "Stopping the refused link after reconnect failed; continuing.");
                }

                // StopAsync sets the link's deliberate-stop flag, which SUPPRESSES
                // the Closed event — so we MUST resolve the closed signal ourselves,
                // otherwise the supervision loop parks on it forever (a zombie agent
                // that reconnected, was refused, and never retries again). Resolving
                // it wakes the loop, which reconnects, gets the same refusal, and
                // slow-lane paces — the intended self-healing behaviour.
                _closedSignal?.TrySetResult(null);
            }
        });

        // ── Supervision loop ──────────────────────────────────────────────
        // Same pacing curve as the in-connection retry policy so operators see one
        // consistent backoff story (incl. the slow 401/403 re-enroll lane).
        //
        // What this loop paces is every cycle that FAILED TO BECOME USEFUL, which is
        // deliberately broader than "StartAsync threw":
        //
        //   * StartAsync threw — unreachable server, refused handshake (426), rejected
        //     token. WithAutomaticReconnect never covers initial start failures.
        //   * StartAsync SUCCEEDED and the server then rejected the connection from inside
        //     the hub. This is the case that was unpaced, and it is the common one: a
        //     rejection in OnConnectedAsync (unknown target, retired target, missing claim,
        //     or simply a throw from a saturated tenant database) happens AFTER the
        //     handshake completes. Measured against a real hub, the client's automatic
        //     reconnect is not involved at all — Reconnecting never fires, Closed does — so
        //     the park below releases and this loop is the only thing that can pace it.
        //     Resetting the counter on a bare StartAsync success made that loop free-running
        //     at round-trip cadence, from every agent at once, against a server already
        //     failing.
        //
        // An ACCEPTED REGISTRATION is therefore what clears the counter, not a successful
        // connect — the same distinction RegistrationOutcome.Accepted documents. A healthy
        // link that closes after a normal server restart has an accepted registration behind
        // it, so it still reconnects immediately; only a cycle that never got one escalates.
        var startBackoff = new AgentReconnectPolicy(logger);
        var unproductiveCycles = 0L;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _closedSignal = new TaskCompletionSource<Exception?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    logger.LogInformation("Connecting to server {ServerUrl}.", serverUrl);

                    // Token as a PROVIDER over AgentContext: the sliding refresh
                    // (A8) swaps Identity for one carrying a fresh token, and
                    // (re)connects must present the current token, not a snapshot.
                    await serverLink
                        .StartAsync(
                            serverUrl,
                            () => context.Identity?.AgentToken,
                            context.Identity?.ReleaseId,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // WithAutomaticReconnect never covers INITIAL start failures
                    // (see AgentReconnectPolicy docs) — this loop is the retry.
                    //
                    // A 426 from the wire-contract gate is recorded on the context, because it
                    // is the one connect failure the agent can fix ITSELF: the self-upgrade
                    // path runs over REST and does not need this connection, and the flag is
                    // what tells it the maintenance window and the connected check are
                    // protecting nothing (AgentContext.ContractRefused). It is set BEFORE the
                    // delay so the very next updater tick can act on it.
                    context.SetContractRefused(IsContractRefusal(ex));
                    unproductiveCycles++;
                    var connectDelay = NextPacingDelay(startBackoff, unproductiveCycles, ex);
                    logger.LogWarning(ex,
                        "Could not connect to server {ServerUrl} (unproductive cycle " +
                        "{Cycle}); retrying in {Delay}.",
                        serverUrl, unproductiveCycles, connectDelay);
                    try
                    {
                        await Task.Delay(connectDelay, timeProvider, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                // Past the gate: whatever else is wrong, the wire contract is not, so the
                // updater's escape hatch closes again.
                context.SetContractRefused(false);
                logger.LogInformation("Connected to server {ServerUrl}.", serverUrl);

                var registration = await TrySendRegistrationAsync(stoppingToken).ConfigureAwait(false);
                if (registration == RegistrationOutcome.Accepted)
                {
                    unproductiveCycles = 0;
                }

                if (registration == RegistrationOutcome.Refused)
                {
                    // A refusal is no longer expected here — the wire-contract skew that
                    // used to produce one is refused on the HANDSHAKE now, so StartAsync
                    // above throws instead and the initial-connect lane paces it. This arm
                    // is kept as a fail-safe for the remaining Accepted:false shapes
                    // (unknown or retired target), which OnConnectedAsync also aborts, and
                    // for any future server-side refusal. Deleting a backstop because the
                    // current code cannot reach it is how this work package acquired most
                    // of its defects.
                    // Stop the link and pace like the auth-failure lane: such a refusal
                    // clears only on operator action, so the normal backoff would be noise.
                    try
                    {
                        await serverLink.StopAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Stopping the refused link failed; continuing.");
                    }
                    try
                    {
                        await Task.Delay(
                                AgentReconnectPolicy.OperatorActionDelay, timeProvider, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                // Park until the link closes PERMANENTLY (transient drops are
                // retried inside the connection and never resolve this signal)
                // or the host shuts down.
                Exception? closeReason;
                try
                {
                    closeReason = await _closedSignal.Task
                        .WaitAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                logger.LogWarning(closeReason,
                    "Server link closed permanently; restarting the connection cycle.");

                // A cycle that never got an accepted registration and has now closed is the
                // server-side-rejection loop: reconnecting with no delay would re-run it at
                // round-trip cadence. A cycle that DID register resets the counter above, so
                // a normal server restart still reconnects immediately.
                if (registration != RegistrationOutcome.Accepted)
                {
                    unproductiveCycles++;
                    var cycleDelay = NextPacingDelay(startBackoff, unproductiveCycles, closeReason);
                    logger.LogWarning(
                        "The link to {ServerUrl} closed without ever completing registration " +
                        "(unproductive cycle {Cycle}); retrying in {Delay}. A server that " +
                        "rejects this agent from inside the hub — unknown or retired target, " +
                        "or a tenant database that cannot answer — looks exactly like this.",
                        serverUrl, unproductiveCycles, cycleDelay);
                    try
                    {
                        await Task.Delay(cycleDelay, timeProvider, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        // No broad catch: an unexpected supervisor crash must NOT leave the
        // process running with a permanently dead link (the exact T0-2 failure
        // this service exists to prevent). Letting it propagate stops the host
        // (BackgroundServiceExceptionBehavior.StopHost, .NET 6+ default) so
        // service-manager recovery restarts the agent — a visible crash-loop
        // beats a silent zombie. The finally still reports shutdown.
        finally
        {
            // Drain BEFORE the link goes down: an unwinding handler still wants to
            // report its aborted completion over it.
            await DrainDetachedHandlersAsync().ConfigureAwait(false);
            await ReportShutdownAndDisconnectAsync().ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// The supervision loop's backoff, expressed through the same
    /// <see cref="AgentReconnectPolicy"/> the in-connection retry uses so operators see one
    /// curve. <paramref name="unproductiveCycles"/> is 1-based (the first failure), and the
    /// policy's attempt 0 is deliberately immediate — so the first retry rides out a blip and
    /// only a repeating failure escalates toward the 30 s cap.
    /// </summary>
    /// <summary>
    /// Whether a failed connect was the wire-contract gate's 426.
    /// <para>
    /// The status code is all there is to go on, and that is deliberate rather than lazy:
    /// <c>HttpConnection.NegotiateAsync</c> calls <c>EnsureSuccessStatusCode()</c> before
    /// reading the response, so the gate's body and its <c>X-KD-Contract-Server</c> header are
    /// both discarded before the agent can see them. Verified by executing a 426 negotiate
    /// against a real client — the exception message carries nothing but the status.
    /// </para>
    /// </summary>
    internal static bool IsContractRefusal(Exception? ex)
        => ex is HttpRequestException { StatusCode: HttpStatusCode.UpgradeRequired };

    private static TimeSpan NextPacingDelay(
        AgentReconnectPolicy backoff, long unproductiveCycles, Exception? reason)
        => backoff.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = unproductiveCycles - 1,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = reason,
        }) ?? AgentReconnectPolicy.MaxDelay;

    /// <summary>
    /// Registers a detached push handler so its failure is visible and shutdown can
    /// drain it. The continuation runs on <see cref="CancellationToken.None"/> — it
    /// must still fire when the host is stopping, which is exactly when a handler is
    /// most likely to fault.
    /// </summary>
    private void TrackDetachedHandler(Task work, string description)
    {
        _detachedHandlers.TryAdd(work, 0);
        _ = work.ContinueWith(
            completed =>
            {
                _detachedHandlers.TryRemove(completed, out _);
                if (completed.IsFaulted)
                {
                    logger.LogError(completed.Exception,
                        "Unhandled error in the detached {Description} handler.", description);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Gives detached handlers <see cref="DetachedHandlerDrainTimeout"/> to finish at
    /// shutdown. Never throws: a handler that is still running past the bound is
    /// reported and abandoned to the server-side lease reconciler.
    /// </summary>
    private async Task DrainDetachedHandlersAsync()
    {
        var pending = _detachedHandlers.Keys.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        logger.LogInformation(
            "Waiting up to {Timeout} for {Count} in-flight push handler(s) to unwind.",
            DetachedHandlerDrainTimeout, pending.Length);
        try
        {
            await Task.WhenAll(pending).WaitAsync(DetachedHandlerDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "{Count} push handler(s) still running after {Timeout}; abandoning them. " +
                "The server's lease reconciler owns any interrupted task.",
                pending.Count(t => !t.IsCompleted), DetachedHandlerDrainTimeout);
        }
        catch (Exception ex)
        {
            // Handler faults are already logged per-handler by TrackDetachedHandler;
            // WhenAll re-throwing them here must not break shutdown.
            logger.LogDebug(ex, "A detached handler faulted during shutdown drain.");
        }
    }

    private enum RegistrationOutcome
    {
        /// <summary>
        /// The server accepted the registration. This — not a successful
        /// <c>StartAsync</c> — is what makes a connection USEFUL, so it is what resets the
        /// supervision loop's backoff. The two were one value once, which meant a connection
        /// that connected cleanly and then failed registration reset the backoff on every
        /// cycle, so a server rejecting the agent from inside the hub (a tenant-DB blip, an
        /// unknown or retired target) got reconnected at round-trip cadence forever, by every
        /// agent at once. Round 4 reverted the distinction on the premise that the client's
        /// automatic reconnect absorbed such a rejection; it does not — a rejection inside
        /// <c>OnConnectedAsync</c> fires <c>Closed</c>, not <c>Reconnecting</c>, which is
        /// pinned by <c>ReconnectE2ETests</c>.
        /// </summary>
        Accepted,

        /// <summary>Failed transiently — re-sent on the next (re)connect. The connection
        /// itself is up and dispatchable (the hub registered it in
        /// <c>OnConnectedAsync</c>), so this does not end the cycle; it only withholds the
        /// backoff reset.</summary>
        Retryable,

        /// <summary>
        /// The server returned <c>Accepted: false</c> — a deterministic refusal that clears
        /// only on operator action, so it paces on the slow lane. Since the wire-contract
        /// check moved to the handshake, the reachable shapes are "unknown target" and
        /// "retired target"; a version skew never gets this far (it is a 426 out of
        /// <c>StartAsync</c>).
        /// </summary>
        Refused,
    }

    /// <summary>
    /// Best-effort registration: a transient failure is logged and NOT fatal to
    /// the connection cycle — the next reconnect re-sends it, and the hub's
    /// OnConnectedAsync has already marked the target Online regardless. A B6
    /// contract-version REFUSAL is different: it is deterministic until the
    /// agent binary changes, so it is surfaced to the caller for slow-lane
    /// pacing instead of a hot retry.
    /// </summary>
    private async Task<RegistrationOutcome> TrySendRegistrationAsync(CancellationToken ct)
    {
        try
        {
            var result = await SendRegistrationAsync(ct).ConfigureAwait(false);
            if (result is { Accepted: false })
            {
                // No "update the agent binary" instruction here. Since the wire-contract
                // check moved onto the handshake, a version skew never reaches this method —
                // it is a 426 out of StartAsync. What CAN reach it is an unknown or retired
                // target, where both contract versions are identical and upgrading the binary
                // fixes nothing. The server's own Message is the actionable part; naming
                // versions alongside it only pointed the operator at the wrong remedy.
                logger.LogError(
                    "Server REFUSED this agent's registration: {Message} This clears only on " +
                    "operator action (re-enroll the target, or un-retire it), so the agent " +
                    "retries on the slow lane until then.",
                    result.Message ?? "no reason given");
                return RegistrationOutcome.Refused;
            }

            // C6 — the server accepted us: this is the post-boot health signal the
            // self-upgrade probation gate waits on. Fires once; harmless on
            // re-registration after a reconnect.
            context.SignalRegistrationAccepted();
            return RegistrationOutcome.Accepted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — nothing to do.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Sending registration failed — will re-send on the next (re)connect.");
        }
        return RegistrationOutcome.Retryable;
    }

    private async Task<AgentRegistrationResult> SendRegistrationAsync(CancellationToken ct)
    {
        var machineInfo = machineCollector.Collect(agentConfig.Value.ResolvedDataPath);

        // T1-7 / B6: roles are authorization (they drive secret scoping) and are
        // assigned OPERATOR-side on the server — the wire field is gone entirely.
        // Warn the operator if the local config still carries roles so they know
        // it has no effect (config removal is a later cleanup).
        if (agentConfig.Value.Roles is { Count: > 0 } configuredRoles)
        {
            logger.LogWarning(
                "Agent config lists {Count} role(s) — these are IGNORED. Target roles " +
                "are assigned server-side (target settings / registration wizard).",
                configuredRoles.Count);
        }

        var request = new AgentRegistrationRequest(
            TargetId: context.Identity!.AgentId,
            MachineName: machineInfo.MachineName,
            OperatingSystem: machineInfo.OperatingSystem,
            AgentVersion: machineInfo.AgentVersion,
            FreeDiskBytes: machineInfo.FreeDiskBytes,
            TotalRamBytes: machineInfo.TotalRamBytes,
            ContractVersion: AgentContract.CurrentVersion);

        var result = await serverLink.RegisterAsync(request, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Sent registration: machine={Machine}, OS={OS}, agent={AgentVersion}, " +
            "freeDisk={FreeDisk} MB, totalRam={TotalRam} MB.",
            machineInfo.MachineName,
            machineInfo.OperatingSystem,
            machineInfo.AgentVersion,
            machineInfo.FreeDiskBytes / 1_048_576,
            machineInfo.TotalRamBytes / 1_048_576);

        return result;
    }

    private async Task ReportShutdownAndDisconnectAsync()
    {
        // Best-effort: give it 5 s then move on regardless.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await serverLink
                .ReportStatusAsync("ShuttingDown", cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "ReportStatus ShuttingDown failed — ignored on shutdown path.");
        }

        try
        {
            await serverLink.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Server link stop failed — ignored on shutdown path.");
        }
    }
}

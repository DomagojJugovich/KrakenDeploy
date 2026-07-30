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
        // Same pacing as the in-connection retry policy so operators see one
        // consistent backoff story (incl. the slow 401/403 re-enroll lane).
        //
        // Paces INITIAL-CONNECT failures only — a refused handshake (426 from the contract
        // gate), an unreachable server, a rejected token. Everything that happens to an
        // ESTABLISHED connection is paced by the policy instance inside the link, which is
        // the only place that observes it.
        var startBackoff = new AgentReconnectPolicy(logger);
        var failedStartAttempts = 0L;

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
                    failedStartAttempts++;
                    var delay = startBackoff.NextRetryDelay(new RetryContext
                    {
                        PreviousRetryCount = failedStartAttempts - 1,
                        ElapsedTime = TimeSpan.Zero,
                        RetryReason = ex,
                    }) ?? AgentReconnectPolicy.MaxDelay;

                    logger.LogWarning(ex,
                        "Could not connect to server {ServerUrl} (attempt {Attempt}); " +
                        "retrying in {Delay}.",
                        serverUrl, failedStartAttempts, delay);
                    try
                    {
                        await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                failedStartAttempts = 0;
                logger.LogInformation("Connected to server {ServerUrl}.", serverUrl);

                var registration = await TrySendRegistrationAsync(stoppingToken).ConfigureAwait(false);
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
                                AgentReconnectPolicy.AuthFailureDelay, timeProvider, stoppingToken)
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
                // Loop immediately — StartAsync failures pace any retries, and a connection
                // that keeps dying moments after it is established is paced by
                // AgentReconnectPolicy's churn lane inside the link itself.
                //
                // A delay was added here in round 3, to pace a server that repeatedly aborts
                // registration. It could never fire: this park is only released by the
                // Closed event, and AgentReconnectPolicy never returns null, so
                // HubConnection never gives up and never raises Closed for a server-side
                // abort — it reconnects internally instead. The pacing therefore has to live
                // where that loop actually runs, which is the policy.
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
        /// <c>StartAsync</c> — is what makes a connection USEFUL, so it is what resets
        /// the supervision loop's backoff. F5: the two used to be one value, which meant
        /// a connection that connected cleanly and then failed registration reset the
        /// backoff on every cycle, so a server that aborts registration (a tenant-DB
        /// blip) got reconnected at RTT cadence forever, by every agent at once.
        /// </summary>
        Accepted,

        /// <summary>Failed transiently — re-sent on the next (re)connect.</summary>
        Retryable,

        /// <summary>B6: the server refused the contract version — the connection
        /// is dispatch-dead until the agent is upgraded; pace on the slow lane.</summary>
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
                logger.LogError(
                    "Server REFUSED this agent's registration: {Message} " +
                    "(server contract v{ServerVersion}, this agent speaks v{AgentVersion}). " +
                    "Update the agent binary; retrying on the slow lane until then.",
                    result.Message ?? "no reason given",
                    result.ServerContractVersion,
                    AgentContract.CurrentVersion);
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

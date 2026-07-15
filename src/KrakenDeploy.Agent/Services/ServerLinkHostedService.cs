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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        serverLink.OnRunDeployment(plan =>
            Task.Run(() => deploymentExecutor.ExecuteAsync(plan), stoppingToken));

        // M11.E.7 — same gate-before-open contract for ad-hoc commands.
        // The executor is fail-closed: refuses on signature mismatch /
        // missing public key, always reports back to the dispatcher.
        serverLink.OnRunAdhocScript(cmd =>
            Task.Run(() => adhocExecutor.HandleAsync(cmd), stoppingToken));

        serverLink.OnClosed(ex =>
        {
            _closedSignal?.TrySetResult(ex);
            return Task.CompletedTask;
        });

        serverLink.OnReconnected(async () =>
        {
            logger.LogInformation(
                "Reconnected to server {ServerUrl}; re-sending registration.", serverUrl);
            await TrySendRegistrationAsync(stoppingToken).ConfigureAwait(false);
        });

        // ── Supervision loop ──────────────────────────────────────────────
        // Same pacing as the in-connection retry policy so operators see one
        // consistent backoff story (incl. the slow 401/403 re-enroll lane).
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

                await TrySendRegistrationAsync(stoppingToken).ConfigureAwait(false);

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
                // Loop immediately — StartAsync failures pace any retries.
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
            await ReportShutdownAndDisconnectAsync().ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort registration: a failure is logged and NOT fatal to the
    /// connection cycle — the next reconnect re-sends it, and the hub's
    /// OnConnectedAsync has already marked the target Online regardless.
    /// </summary>
    private async Task TrySendRegistrationAsync(CancellationToken ct)
    {
        try
        {
            await SendRegistrationAsync(ct).ConfigureAwait(false);
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
    }

    private async Task SendRegistrationAsync(CancellationToken ct)
    {
        var machineInfo = machineCollector.Collect(agentConfig.Value.ResolvedDataPath);

        // T1-7: roles are authorization (they drive secret scoping) and are
        // assigned OPERATOR-side on the server, never self-declared by the agent.
        // Send an empty list; the server ignores any roles it receives (and audits
        // the attempt). Warn the operator if the local config still carries roles
        // so they know it now has no effect (config removal is a later cleanup).
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
            Roles: [],
            FreeDiskBytes: machineInfo.FreeDiskBytes,
            TotalRamBytes: machineInfo.TotalRamBytes);

        await serverLink.RegisterAsync(request, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Sent registration: machine={Machine}, OS={OS}, agent={AgentVersion}, " +
            "freeDisk={FreeDisk} MB, totalRam={TotalRam} MB.",
            machineInfo.MachineName,
            machineInfo.OperatingSystem,
            machineInfo.AgentVersion,
            machineInfo.FreeDiskBytes / 1_048_576,
            machineInfo.TotalRamBytes / 1_048_576);
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

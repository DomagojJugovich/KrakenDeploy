using KrakenDeploy.Agent.Adhoc;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Opens and maintains the SignalR control-plane connection to the server.
/// Waits for <see cref="AgentContext.IdentityReady"/> before connecting so it
/// always has a valid bearer token.
/// On shutdown it reports <c>ShuttingDown</c> status before disconnecting.
/// </summary>
public sealed class ServerLinkHostedService(
    AgentContext context,
    IServerLink serverLink,
    DeploymentExecutor deploymentExecutor,
    AdhocScriptExecutor adhocExecutor,
    MachineInfoCollector machineCollector,
    IOptions<ServerOptions> serverOptions,
    IOptions<AgentConfig> agentConfig,
    ILogger<ServerLinkHostedService> logger)
    : BackgroundService
{
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

        var identity = context.Identity!;
        var serverUrl = serverOptions.Value.Url;

        logger.LogInformation("Connecting to server {ServerUrl}.", serverUrl);

        try
        {
            // Register deployment handler BEFORE opening the connection so no
            // RunDeploymentAsync messages can arrive before the handler is wired.
            serverLink.OnRunDeployment(plan =>
                Task.Run(() => deploymentExecutor.ExecuteAsync(plan), stoppingToken));

            // M11.E.7 — same gate-before-open contract for ad-hoc commands.
            // The executor is fail-closed: refuses on signature mismatch /
            // missing public key, always reports back to the dispatcher.
            serverLink.OnRunAdhocScript(cmd =>
                Task.Run(() => adhocExecutor.HandleAsync(cmd), stoppingToken));

            await serverLink
                .StartAsync(serverUrl, identity.AgentToken, stoppingToken)
                .ConfigureAwait(false);

            logger.LogInformation("Connected to server {ServerUrl}.", serverUrl);

            await SendRegistrationAsync(identity, stoppingToken).ConfigureAwait(false);

            // Hold open until the host signals shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — fall through to finally.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Server link failed unexpectedly.");
        }
        finally
        {
            await ReportShutdownAndDisconnectAsync().ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task SendRegistrationAsync(AgentIdentity identity, CancellationToken ct)
    {
        var machineInfo = machineCollector.Collect(agentConfig.Value.ResolvedDataPath);

        var request = new AgentRegistrationRequest(
            TargetId: identity.AgentId,
            MachineName: machineInfo.MachineName,
            OperatingSystem: machineInfo.OperatingSystem,
            AgentVersion: machineInfo.AgentVersion,
            Roles: agentConfig.Value.Roles,
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

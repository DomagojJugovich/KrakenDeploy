using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Sends a <see cref="HeartbeatRequest"/> to the server hub every 30 seconds.
/// Waits for <see cref="AgentContext.IdentityReady"/> before starting its loop;
/// skips ticks when the connection is not yet established.
/// </summary>
public sealed class HeartbeatHostedService(
    AgentContext context,
    IServerLink serverLink,
    MachineInfoCollector machineCollector,
    IOptions<AgentConfig> agentConfig,
    ILogger<HeartbeatHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until identity is resolved before we start sending heartbeats.
        try
        {
            await context.IdentityReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!serverLink.IsConnected)
            {
                logger.LogDebug("Heartbeat skipped — not connected.");
                continue;
            }

            try
            {
                var machineInfo = machineCollector.Collect(agentConfig.Value.ResolvedDataPath);

                var request = new HeartbeatRequest(
                    MachineName: machineInfo.MachineName,
                    OperatingSystem: machineInfo.OperatingSystem,
                    AgentVersion: machineInfo.AgentVersion,
                    FreeDiskBytes: machineInfo.FreeDiskBytes);

                await serverLink.HeartbeatAsync(request, stoppingToken).ConfigureAwait(false);

                logger.LogDebug(
                    "Heartbeat sent. FreeDisk={FreeDisk} MB.",
                    machineInfo.FreeDiskBytes / 1_048_576);
            }
            catch (OperationCanceledException)
            {
                throw; // let BackgroundService unwind cleanly
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Heartbeat failed — will retry next tick.");
            }
        }
    }
}

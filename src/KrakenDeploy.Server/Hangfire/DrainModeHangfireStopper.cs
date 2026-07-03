using KrakenDeploy.ControlPlane.Releases;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Blue-green drain compliance for background work
/// (docs/blue-green-slot-deployment.md §8 step 6): when THIS instance's release
/// turns Draining, stop this instance's Hangfire server so it stops competing
/// for new jobs on the shared storage — the Active release's instances keep the
/// schedule complete (no ticks are lost; they simply run elsewhere). In-flight
/// deployment orchestration is unaffected (it rides the in-process channel, not
/// Hangfire) and finishes naturally per §9.
/// <para>
/// One-way by design: a release that started draining never becomes the default
/// again (registry invariant), so there is nothing to restart. Registered in
/// multi-account mode only.
/// </para>
/// </summary>
public sealed class DrainModeHangfireStopper(
    SlotDrainGuard drainGuard,
    IServiceProvider services,
    ILogger<DrainModeHangfireStopper> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!await drainGuard.IsOwnReleaseDrainingAsync(stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }

                // AddHangfireServer registers an internal hosted service; match it
                // by type name — the only stable handle Hangfire.AspNetCore exposes.
                // Resolved LAZILY here (never via the ctor): a hosted service that
                // ctor-injects IEnumerable<IHostedService> is a circular dependency.
                var hangfire = services.GetServices<IHostedService>().FirstOrDefault(
                    s => s.GetType().FullName?.Contains("Hangfire", StringComparison.Ordinal) == true);
                if (hangfire is null)
                {
                    logger.LogWarning(
                        "Own release is Draining but no Hangfire hosted service was found to stop.");
                    return;
                }

                logger.LogInformation(
                    "Own release is Draining — stopping this instance's Hangfire server so new " +
                    "background work runs on the Active release's instances.");
                await hangfire.StopAsync(stoppingToken).ConfigureAwait(false);
                return; // One-way: drained releases never come back.
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}

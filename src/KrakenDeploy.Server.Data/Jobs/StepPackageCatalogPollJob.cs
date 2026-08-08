using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: refreshes the step-package catalog from the
/// configured GitHub Releases feed (default <c>DomagojJugovich/kraken-steps</c>
/// since SC6). Wraps <see cref="StepPackageCatalogService.RefreshAsync"/> and
/// swallows network failures so a flaky GitHub doesn't fail the job and
/// trigger Hangfire retries.
/// <para>
/// SC6: gated on the <c>feeds.step-package-catalog</c> feature flag — the
/// runtime kill-switch (the <c>StepPackages:Catalog:Enabled</c> config key is
/// the deployment-posture switch, checked inside RefreshAsync). The flag's
/// catalog entry always documented this gate; the check actually exists now.
/// </para>
/// </summary>
public sealed class StepPackageCatalogPollJob(
    StepPackageCatalogService catalog,
    FeatureFlagService featureFlags,
    ILogger<StepPackageCatalogPollJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (!await featureFlags.IsEnabledAsync("feeds.step-package-catalog", ct)
                    .ConfigureAwait(false))
            {
                logger.LogDebug(
                    "Step-package catalog poll skipped — feeds.step-package-catalog is off.");
                return;
            }

            await catalog.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warning, not error — Hangfire won't retry; the next hourly
            // tick picks up. Catalog stays whatever it was on last success.
            logger.LogWarning(ex,
                "Step-package catalog refresh failed; will retry next hour.");
        }
    }
}

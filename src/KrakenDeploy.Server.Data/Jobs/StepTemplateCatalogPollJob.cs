using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: refreshes the community step-template catalog from
/// the configured GitHub feeds (SC6 — multi-feed). Wraps
/// <see cref="StepTemplateCatalogService.RefreshAsync"/> and swallows network
/// failures so a flaky GitHub doesn't fail the job and trigger Hangfire retries.
/// <para>
/// SC6: gated on the <c>feeds.step-template-catalog</c> feature flag — the
/// runtime kill-switch (the <c>StepTemplates:Catalog:Enabled</c> config key is
/// the deployment-posture switch, checked inside RefreshAsync). The flag's
/// catalog entry always documented this gate; the check actually exists now.
/// </para>
/// </summary>
public sealed class StepTemplateCatalogPollJob(
    StepTemplateCatalogService catalog,
    FeatureFlagService featureFlags,
    ILogger<StepTemplateCatalogPollJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (!await featureFlags.IsEnabledAsync("feeds.step-template-catalog", ct)
                    .ConfigureAwait(false))
            {
                logger.LogDebug(
                    "Step-template catalog poll skipped — feeds.step-template-catalog is off.");
                return;
            }

            await catalog.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged at warning so a temporary GitHub outage doesn't spam errors.
            // Hangfire won't retry; the next hourly tick will pick up.
            logger.LogWarning(ex,
                "Step-template catalog refresh failed; will retry next hour.");
        }
    }
}

using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: refreshes the step-package catalog from the
/// configured GitHub Releases feed (default <c>KrakenDeploy/StepPackages</c>).
/// Wraps <see cref="StepPackageCatalogService.RefreshAsync"/> and swallows
/// network failures so a flaky GitHub doesn't fail the job and trigger
/// Hangfire retries.
/// </summary>
public sealed class StepPackageCatalogPollJob(
    StepPackageCatalogService catalog,
    ILogger<StepPackageCatalogPollJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
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

using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: refreshes the community step-template catalog from
/// the OctopusDeploy/Library GitHub repo. Wraps
/// <see cref="StepTemplateCatalogService.RefreshAsync"/> and swallows network
/// failures so a flaky GitHub doesn't fail the job and trigger Hangfire retries.
/// </summary>
public sealed class StepTemplateCatalogPollJob(
    StepTemplateCatalogService catalog,
    ILogger<StepTemplateCatalogPollJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
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

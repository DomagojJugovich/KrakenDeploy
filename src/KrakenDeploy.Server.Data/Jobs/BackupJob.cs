using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire entry point for the scheduled backup recurring job (M13.G).
/// Job ID <c>kraken.backup</c>. Registered or removed by
/// <c>BackupSchedulerService</c> based on <c>BackupSettings.ScheduleEnabled</c>
/// — the cron expression isn't hard-coded into <c>HangfireJobRegistrar</c>
/// because the operator owns it.
///
/// <para>
/// Pauses when maintenance mode is on (M13.A.3) — a backup that fires
/// mid-upgrade would race the migration and produce a half-state bundle.
/// </para>
/// </summary>
public sealed class BackupJob(
    KrakenDeploy.Server.Data.Services.BackupService backupService,
    MaintenancePause maintenancePause,
    ILogger<BackupJob> logger)
{
    /// <summary>Hangfire recurring-job constant — used by both the
    /// registration call and the page's "next run" indicator.</summary>
    public const string RecurringJobId = "kraken.backup";

    /// <summary>Triggered-by label written to BackupRun.TriggeredBy when
    /// the job fires automatically (as opposed to a manual UI click).</summary>
    public const string TriggeredByLabel = "Schedule";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (await maintenancePause.ShouldPauseAsync(ct, logger, RecurringJobId)
            .ConfigureAwait(false))
        {
            return;
        }
        await backupService.RunOnceAsync(TriggeredByLabel, ct).ConfigureAwait(false);
    }
}

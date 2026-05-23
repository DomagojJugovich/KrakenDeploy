using global::Hangfire;
using KrakenDeploy.Server.Core.Domain.Backup;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Registers or removes the <c>kraken.backup</c> recurring job based on the
/// operator's <see cref="BackupSettings"/> choices. Unlike the other
/// recurring jobs (audit retention, agent offline sweep) the backup cron
/// isn't hard-coded in <see cref="HangfireJobRegistrar"/> at startup —
/// the operator owns it, so this helper runs at startup AND every time
/// the settings page saves.
/// </summary>
public sealed class BackupScheduler(
    BackupService backupService,
    IRecurringJobManager recurringJobs,
    ILogger<BackupScheduler> logger)
{
    /// <summary>
    /// Aligns Hangfire's recurring-jobs table with the persisted
    /// <see cref="BackupSettings"/>: registers the job when
    /// <see cref="BackupSettings.ScheduleEnabled"/> is true + a valid cron
    /// is set, removes it otherwise. Safe to call repeatedly — Hangfire's
    /// AddOrUpdate is idempotent and RemoveIfExists is no-op when nothing
    /// is registered.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var settings = await backupService.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.ScheduleEnabled || string.IsNullOrWhiteSpace(settings.ScheduleCron))
        {
            recurringJobs.RemoveIfExists(BackupJob.RecurringJobId);
            logger.LogInformation(
                "Backup schedule disabled — recurring job {JobId} removed.",
                BackupJob.RecurringJobId);
            return;
        }

        try
        {
            recurringJobs.AddOrUpdate<BackupJob>(
                BackupJob.RecurringJobId,
                job => job.ExecuteAsync(CancellationToken.None),
                settings.ScheduleCron,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            logger.LogInformation(
                "Backup schedule applied: cron={Cron}", settings.ScheduleCron);
        }
        catch (Exception ex)
        {
            // Hangfire throws on invalid cron — surface the error to the
            // caller so the UI can show "schedule rejected: <reason>" on
            // save instead of silently failing.
            logger.LogError(ex,
                "Failed to register backup schedule with cron '{Cron}'", settings.ScheduleCron);
            throw;
        }
    }
}

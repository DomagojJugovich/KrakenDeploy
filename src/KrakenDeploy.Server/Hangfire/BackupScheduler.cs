using global::Hangfire;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Backup;
using KrakenDeploy.Server.Core.Domain.Platform;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Registers or removes the backup recurring job based on the operator's
/// <see cref="BackupSettings"/> choices. Unlike the other recurring jobs (audit
/// retention, agent offline sweep) the backup cron isn't hard-coded in
/// <see cref="HangfireJobRegistrar"/> at startup — the operator owns it, so this helper
/// runs at startup AND every time the settings page saves.
/// <para>
/// Multi-account: each account owns a per-account recurring job
/// (<c>kraken.backup:{accountId}</c>) that backs up its own tenant DB under
/// <c>WithAccount</c> via <see cref="AccountBackupRunner"/>; the active account comes from
/// <see cref="IAccountContext"/> (the settings page runs in that account's circuit, the
/// startup reconcile runs each account under <c>WithAccount</c>). Single-instance keeps the
/// single <c>kraken.backup</c> job running <see cref="BackupJob"/> directly.
/// </para>
/// </summary>
public sealed class BackupScheduler(
    BackupService backupService,
    IRecurringJobManager recurringJobs,
    IAccountContext accountContext,
    DeploymentOptions deploymentOptions,
    ILogger<BackupScheduler> logger)
{
    /// <summary>
    /// Aligns Hangfire's recurring-jobs table with the persisted
    /// <see cref="BackupSettings"/> for the current scope: registers the job when
    /// <see cref="BackupSettings.ScheduleEnabled"/> is true + a valid cron is set, removes
    /// it otherwise. Safe to call repeatedly (AddOrUpdate is idempotent; RemoveIfExists is
    /// a no-op when nothing is registered).
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var settings = await backupService.GetSettingsAsync(ct).ConfigureAwait(false);

        // Saas → a per-account job id + runner (backs up the active account's
        // tenant DB). Single-tenant topologies → the single job running BackupJob directly.
        var perAccount = deploymentOptions.Topology == DeploymentTopology.Saas
            && accountContext.IsResolved;
        var jobId = perAccount
            ? $"{BackupJob.RecurringJobId}:{accountContext.CurrentAccountId}"
            : BackupJob.RecurringJobId;

        if (!settings.ScheduleEnabled || string.IsNullOrWhiteSpace(settings.ScheduleCron))
        {
            recurringJobs.RemoveIfExists(jobId);
            logger.LogInformation(
                "Backup schedule disabled — recurring job {JobId} removed.", jobId);
            return;
        }

        try
        {
            if (perAccount)
            {
                var accountId = accountContext.CurrentAccountId;
                recurringJobs.AddOrUpdate<AccountBackupRunner>(
                    jobId,
                    runner => runner.RunForAccountAsync(accountId, CancellationToken.None),
                    settings.ScheduleCron,
                    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            }
            else
            {
                recurringJobs.AddOrUpdate<BackupJob>(
                    jobId,
                    job => job.ExecuteAsync(CancellationToken.None),
                    settings.ScheduleCron,
                    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            }

            logger.LogInformation(
                "Backup schedule applied: job={JobId} cron={Cron}", jobId, settings.ScheduleCron);
        }
        catch (Exception ex)
        {
            // Hangfire throws on invalid cron — surface it so the UI can show
            // "schedule rejected: <reason>" on save instead of silently failing.
            logger.LogError(ex,
                "Failed to register backup schedule {JobId} with cron '{Cron}'",
                jobId, settings.ScheduleCron);
            throw;
        }
    }
}

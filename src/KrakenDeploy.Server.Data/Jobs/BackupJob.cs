namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire entry point for the scheduled backup recurring job (M13.G).
/// Job ID <c>kraken.backup</c>. Registered or removed by
/// <c>BackupSchedulerService</c> based on <c>BackupSettings.ScheduleEnabled</c>
/// — the cron expression isn't hard-coded into <c>HangfireJobRegistrar</c>
/// because the operator owns it.
///
/// <para>
/// The job class lives in <c>KrakenDeploy.Server.Data.Jobs</c> for parity
/// with the other Hangfire jobs (AuditRetentionJob, etc.); the actual
/// backup engine + service live in <c>KrakenDeploy.Server.Services</c>,
/// so this class is a thin marker that the registrar's
/// <c>RecurringJob.AddOrUpdate&lt;T&gt;</c> can type-bind to. The job runs
/// in a Hangfire scope which auto-resolves <c>BackupService</c> at fire time.
/// </para>
/// </summary>
public sealed class BackupJob(
    KrakenDeploy.Server.Data.Services.BackupService backupService)
{
    /// <summary>Hangfire recurring-job constant — used by both the
    /// registration call and the page's "next run" indicator.</summary>
    public const string RecurringJobId = "kraken.backup";

    /// <summary>Triggered-by label written to BackupRun.TriggeredBy when
    /// the job fires automatically (as opposed to a manual UI click).</summary>
    public const string TriggeredByLabel = "Schedule";

    public Task ExecuteAsync(CancellationToken ct)
        => backupService.RunOnceAsync(TriggeredByLabel, ct);
}

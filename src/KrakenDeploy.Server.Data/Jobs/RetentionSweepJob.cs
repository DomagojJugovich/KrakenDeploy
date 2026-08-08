using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job (WP9): the scheduled retention sweep. Where the
/// event-driven <c>RetentionService.PruneAfter*</c> paths only fire on task
/// completion (and only within the just-finished project/runbook), this sweep
/// walks every Space and applies the full retention policy — deployments,
/// releases, reference-protected packages, runbook runs, aged step logs, the
/// orphaned live-log sweep, and on-disk artifact / drop-bundle cleanup — so
/// history that the event path never saw (imported rows, rows pre-dating a
/// keep-count change) is still bounded and no files are orphaned.
///
/// <para>
/// <strong>Dry-run first.</strong> The <c>retention.sweep-dry-run</c> feature
/// flag defaults ON, so a fresh install's sweep computes and audit-logs exactly
/// what it WOULD delete and deletes nothing. Operators verify the prune set on
/// their real history, then flip the flag off to let it apply. The event-driven
/// prune is unaffected and always applies.
/// </para>
///
/// <para>
/// Every run writes one <c>Retention.SweepCompleted</c> audit entry carrying the
/// per-category count summary (and the dry-run flag), so the audit log is the
/// operator's chronology of what the sweep did — or, in dry-run, would have done.
/// </para>
/// </summary>
public sealed class RetentionSweepJob(
    RetentionService retention,
    SettingsService settings,
    FeatureFlagService featureFlags,
    IAuditLog auditLog,
    ILogger<RetentionSweepJob> logger)
{
    /// <summary>Feature-flag key — <c>retention.sweep-dry-run</c>. Default ON.</summary>
    public const string DryRunFeatureKey = "retention.sweep-dry-run";

    /// <summary>Stable recurring-job id used by both the single-instance and the
    /// per-account fan-out registrations in <c>HangfireJobRegistrar</c>.</summary>
    public const string RecurringJobId = "kraken.retention-sweep";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var perf = await settings.GetAsync<PerformanceSettings>(ct: ct).ConfigureAwait(false);
        var dryRun = await featureFlags.IsEnabledAsync(DryRunFeatureKey, ct).ConfigureAwait(false);

        var options = new RetentionSweepOptions
        {
            PackageKeepVersions = perf.PackageRetentionKeepVersions,
            RunbookRunKeep      = perf.RunbookRunRetentionKeep,
            TaskLogAgeDays      = perf.TaskLogRetentionDays,
            DryRun              = dryRun,
        };

        var result = await retention.RunSweepAsync(options, ct).ConfigureAwait(false);

        // One summary entry per run — the operator's preview in dry-run mode and
        // the forensic record once applying. ExecuteDelete bypasses the audit
        // interceptor, so this explicit entry is the only per-category trail.
        await auditLog.RecordAsync(
            AuditEventType.RetentionSweepCompleted,
            subjectType: "RetentionSweep",
            details:     result.ToSummary(),
            ct:          ct).ConfigureAwait(false);

        logger.LogInformation(
            "RetentionSweep ({Mode}): {Summary}.",
            dryRun ? "dry-run" : "apply", result.ToSummary());
    }
}

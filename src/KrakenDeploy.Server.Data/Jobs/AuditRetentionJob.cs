using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: purge audit entries older than the configured
/// retention window. Scheduled nightly by <c>HangfireJobRegistrar</c>.
///
/// <para>
/// Retention window precedence (DB → config → default), all in days:
/// <list type="number">
///   <item><see cref="PerformanceSettings.AuditLogRetentionDays"/> from
///         the singleton row, when present (set via the Performance
///         page — M13.F.3).</item>
///   <item><c>Retention:AuditLogDays</c> in appsettings.json (legacy /
///         bootstrap path; still supported).</item>
///   <item><see cref="DefaultRetentionDays"/> (365) when neither is set.</item>
/// </list>
/// Zero or negative disables purging — every row is kept indefinitely.
/// </para>
///
/// <para>
/// The M13.F.5 <c>audit.purge-enabled</c> feature flag is a master
/// kill-switch checked BEFORE the day-count: when the flag is off, the
/// job short-circuits regardless of the configured day-count. Lets
/// operators pause the purge temporarily (e.g. during a regulatory
/// investigation) without losing the configured value.
/// </para>
///
/// <para>
/// <strong>GDPR posture:</strong> the audit table is a likely target for
/// data-subject access + erasure requests. Bounding it at 365 days by
/// default keeps the table queryable + the erasure scope predictable.
/// </para>
/// </summary>
public sealed class AuditRetentionJob(
    Services.AuditLogService auditLog,
    IDbContextFactory<KrakenDbContext> dbFactory,
    PerformanceSettingsService performance,
    FeatureFlagService featureFlags,
    IConfiguration config,
    ILogger<AuditRetentionJob> logger)
{
    /// <summary>Default retention window applied when nothing else is set.</summary>
    public const int DefaultRetentionDays = 365;

    /// <summary>Configuration key — <c>Retention:AuditLogDays</c>. Kept as
    /// the bootstrap-before-page-save path; the page-save promotes the
    /// value into <see cref="PerformanceSettings"/> which then wins.</summary>
    public const string RetentionDaysConfigKey = "Retention:AuditLogDays";

    /// <summary>Feature-flag key — <c>audit.purge-enabled</c>.</summary>
    public const string PurgeEnabledFeatureKey = "audit.purge-enabled";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // ── Kill-switch (M13.F.5) ────────────────────────────────────────
        var enabled = await featureFlags
            .IsEnabledAsync(PurgeEnabledFeatureKey, ct)
            .ConfigureAwait(false);
        if (!enabled)
        {
            logger.LogInformation(
                "AuditRetention: skipped — feature '{Key}' is off.",
                PurgeEnabledFeatureKey);
            return;
        }

        // ── DB → config → default precedence ─────────────────────────────
        var settings = await performance.GetAsync(ct).ConfigureAwait(false);
        var days = await ResolveRetentionDaysAsync(settings, ct).ConfigureAwait(false);

        if (days <= 0)
        {
            logger.LogInformation(
                "AuditRetention: skipped — retention is {Days} (≤ 0 disables purging).",
                days);
            return;
        }

        var deleted = await auditLog
            .PurgeOldEntriesAsync(days, ct)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            logger.LogInformation(
                "AuditRetention: deleted {Count} entries older than {Days} days.",
                deleted, days);
        }
    }

    /// <summary>
    /// Resolves the effective retention day-count. DB row wins when a
    /// PerformanceSettings row has been saved (we treat "row exists" via
    /// a separate probe to distinguish "operator never visited the page"
    /// from "operator explicitly saved a 0"). On fresh installs the
    /// service returns a transient default object whose
    /// <see cref="PerformanceSettings.AuditLogRetentionDays"/> is the
    /// hardcoded 365 — we want to honour the appsettings value in that
    /// case, not the hardcoded default, so config still works pre-save.
    /// </summary>
    private async Task<int> ResolveRetentionDaysAsync(
        PerformanceSettings settings, CancellationToken ct)
    {
        // Has the operator saved the Performance page at least once?
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rowExists = await db.PerformanceSettings
            .AsNoTracking()
            .AnyAsync(p => p.Id == PerformanceSettings.SingletonId, ct)
            .ConfigureAwait(false);
        if (rowExists)
        {
            return settings.AuditLogRetentionDays;
        }

        // No row yet — fall back to appsettings, then to the hardcoded default.
        return config.GetValue<int?>(RetentionDaysConfigKey) ?? DefaultRetentionDays;
    }
}

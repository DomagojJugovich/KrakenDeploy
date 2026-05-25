using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: purge <c>ai_call_logs</c> rows older than the
/// configured retention window. Scheduled nightly by <c>HangfireJobRegistrar</c>.
/// <para>
/// Window is read at run time from <c>Retention:AiCallLogDays</c>
/// (appsettings.json or env var). Default 90 days — substantially shorter
/// than the standard audit log because AI call rows can include full
/// prompt + response bodies (gated by <c>SpaceAiSettings.LogPromptBodies</c>)
/// which are GDPR-relevant payloads we want bounded by default.
/// </para>
/// <para>
/// Zero or negative disables purging — every row is kept indefinitely.
/// Operators that want to keep AI call history for longer (cost
/// reporting, capacity planning, prompt-template auditing) raise the
/// number explicitly.
/// </para>
/// <para>
/// <strong>Why a separate job from <see cref="AuditRetentionJob"/>:</strong>
/// the two tables have different growth rates + different sensitivity
/// profiles. <c>audit_entries</c> grows at ~user-action rate and rarely
/// stores secrets (the interceptor records before/after JSON of EF
/// entities, which goes through the standard `ISpaceScoped` filter).
/// <c>ai_call_logs</c> grows at deployment + assistant-interaction rate
/// and may store full prompt bodies. Sized them independently so
/// operators can tune each to their environment.
/// </para>
/// </summary>
public sealed class AiCallLogRetentionJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    PerformanceSettingsService performance,
    IConfiguration config,
    TimeProvider time,
    ILogger<AiCallLogRetentionJob> logger)
{
    /// <summary>Default retention window applied when nothing else is set.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>Configuration key — <c>Retention:AiCallLogDays</c>. Kept as
    /// the bootstrap-before-page-save path; the page-save promotes the
    /// value into <see cref="PerformanceSettings"/> which then wins.</summary>
    public const string RetentionDaysConfigKey = "Retention:AiCallLogDays";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // ── DB → config → default precedence (same shape as AuditRetentionJob) ──
        var settings = await performance.GetAsync(ct).ConfigureAwait(false);
        var days = await ResolveRetentionDaysAsync(settings, ct).ConfigureAwait(false);

        if (days <= 0)
        {
            logger.LogInformation(
                "AiCallLogRetention: skipped — retention is {Days} (≤ 0 disables purging).",
                days);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var cutoff = time.GetUtcNow().AddDays(-days);

        // IgnoreQueryFilters — retention runs system-wide, not scoped to a
        // single Space. The audit_log retention does the same.
        var deleted = await db.AiCallLogs
            .IgnoreQueryFilters()
            .Where(x => x.CreatedUtc < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            logger.LogInformation(
                "AiCallLogRetention: deleted {Count} ai_call_logs rows older than {Days} days.",
                deleted, days);
        }
    }

    /// <summary>
    /// Same precedence rule as <see cref="AuditRetentionJob"/>: DB row wins
    /// when the operator has saved the Performance page; appsettings is the
    /// pre-save bootstrap; hardcoded 90 days is the final fallback.
    /// </summary>
    private async Task<int> ResolveRetentionDaysAsync(
        PerformanceSettings settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rowExists = await db.PerformanceSettings
            .AsNoTracking()
            .AnyAsync(p => p.Id == PerformanceSettings.SingletonId, ct)
            .ConfigureAwait(false);
        if (rowExists)
        {
            return settings.AiCallLogRetentionDays;
        }

        return config.GetValue<int?>(RetentionDaysConfigKey) ?? DefaultRetentionDays;
    }
}

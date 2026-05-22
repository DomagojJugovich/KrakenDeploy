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
    IConfiguration config,
    TimeProvider time,
    ILogger<AiCallLogRetentionJob> logger)
{
    /// <summary>Default retention window applied when no config value is set.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>Configuration key — <c>Retention:AiCallLogDays</c>.</summary>
    public const string RetentionDaysConfigKey = "Retention:AiCallLogDays";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var days = config.GetValue<int?>(RetentionDaysConfigKey) ?? DefaultRetentionDays;

        if (days <= 0)
        {
            logger.LogInformation(
                "AiCallLogRetention: skipped — {Key} is {Days} (≤ 0 disables purging).",
                RetentionDaysConfigKey, days);
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
}

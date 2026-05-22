using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: purge audit entries older than the configured
/// retention window. Scheduled nightly by <c>HangfireJobRegistrar</c>.
/// <para>
/// Window is read at run time from <c>Retention:AuditLogDays</c>
/// (appsettings.json or env var). Default 365 days. Zero or negative
/// disables purging — every row is kept indefinitely.
/// </para>
/// <para>
/// <strong>GDPR posture:</strong> the audit table is a likely target for
/// data-subject access + erasure requests. Bounding it at 365 days by
/// default keeps the table queryable + the erasure scope predictable.
/// Operators that need a longer regulatory window (e.g. 7 years for
/// some financial jurisdictions) override via the config key. Operators
/// that want NO automatic purge for forensic reasons set the value to 0.
/// </para>
/// </summary>
public sealed class AuditRetentionJob(
    Services.AuditLogService auditLog,
    IConfiguration config,
    ILogger<AuditRetentionJob> logger)
{
    /// <summary>Default retention window applied when no config value is set.</summary>
    public const int DefaultRetentionDays = 365;

    /// <summary>Configuration key — <c>Retention:AuditLogDays</c>.</summary>
    public const string RetentionDaysConfigKey = "Retention:AuditLogDays";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var days = config.GetValue<int?>(RetentionDaysConfigKey) ?? DefaultRetentionDays;

        if (days <= 0)
        {
            logger.LogInformation(
                "AuditRetention: skipped — {Key} is {Days} (≤ 0 disables purging).",
                RetentionDaysConfigKey, days);
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
}

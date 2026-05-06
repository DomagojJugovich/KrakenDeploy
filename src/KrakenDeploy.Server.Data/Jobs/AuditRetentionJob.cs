using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: purge audit entries older than the configured
/// retention window (default 365 days).
/// Scheduled nightly by <c>HangfireJobRegistrar</c>.
/// </summary>
public sealed class AuditRetentionJob(
    Services.AuditLogService auditLog,
    ILogger<AuditRetentionJob> logger)
{
    private const int DefaultRetentionDays = 365;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var deleted = await auditLog
            .PurgeOldEntriesAsync(DefaultRetentionDays, ct)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            logger.LogInformation(
                "AuditRetention: deleted {Count} entries older than {Days} days.",
                deleted, DefaultRetentionDays);
        }
    }
}

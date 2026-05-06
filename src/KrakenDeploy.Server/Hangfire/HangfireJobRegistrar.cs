using Hangfire;
using KrakenDeploy.Server.Data.Jobs;

namespace KrakenDeploy.Server.Hangfire;

/// <summary>
/// Registers all KrakenDeploy recurring Hangfire jobs.
/// Call once after <c>WebApplication.Build()</c> and before <c>app.Run()</c>
/// so the Hangfire storage is fully initialised.
/// </summary>
public static class HangfireJobRegistrar
{
    public static void RegisterRecurringJobs()
    {
        var utc = TimeZoneInfo.Utc;

        // Purge audit log entries older than 365 days — 03:00 UTC daily.
        RecurringJob.AddOrUpdate<AuditRetentionJob>(
            "kraken.audit-retention",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(3, 0),
            new RecurringJobOptions { TimeZone = utc });

        // Mark stale Online targets as Offline — every 5 minutes.
        // Catches agents that went quiet after a server restart when the
        // in-process grace-period task was lost.
        RecurringJob.AddOrUpdate<AgentLastSeenOfflineJob>(
            "kraken.agent-last-seen-offline",
            job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *",
            new RecurringJobOptions { TimeZone = utc });

        // Clear registration tokens that have expired without being used — 02:00 UTC daily.
        RecurringJob.AddOrUpdate<RegistrationTokenExpiryJob>(
            "kraken.registration-token-expiry",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(2, 0),
            new RecurringJobOptions { TimeZone = utc });

        // Dispatch scheduled deployments whose time has arrived — every minute.
        RecurringJob.AddOrUpdate<ScheduledDeploymentDispatchJob>(
            "kraken.scheduled-deployment-dispatch",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = utc });
    }
}

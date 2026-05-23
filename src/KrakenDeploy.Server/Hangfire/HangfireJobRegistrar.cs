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

        // Purge audit_entries older than Retention:AuditLogDays (default 365).
        // 03:00 UTC daily.
        RecurringJob.AddOrUpdate<AuditRetentionJob>(
            "kraken.audit-retention",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(3, 0),
            new RecurringJobOptions { TimeZone = utc });

        // Purge ai_call_logs older than Retention:AiCallLogDays (default 90).
        // Tighter default than audit retention because AI call rows can
        // carry full prompt + response bodies (GDPR-relevant payloads) —
        // see AiCallLogRetentionJob's class-level comment for the
        // why-different-from-audit reasoning. 03:15 UTC daily so it doesn't
        // collide with the audit sweep.
        RecurringJob.AddOrUpdate<AiCallLogRetentionJob>(
            "kraken.ai-call-log-retention",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(3, 15),
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

        // Refresh the community step-template catalog from the
        // OctopusDeploy/Library GitHub repo — hourly.
        // Uses the Git Trees API (single request) + raw URLs (off-limit), so
        // hourly polling stays comfortably within GitHub's 60-req/hour
        // unauthenticated rate budget.
        RecurringJob.AddOrUpdate<StepTemplateCatalogPollJob>(
            "kraken.step-template-catalog-poll",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly(),
            new RecurringJobOptions { TimeZone = utc });

        // Step-package catalog (Phase D-9) — defaults to KrakenDeploy/StepPackages,
        // configurable via StepPackages:Catalog:Owner / .Repo. Uses the same
        // kraken.github named HttpClient + optional GitHub:Token as the
        // step-template catalog above; one /releases call per hour is cheap
        // even on the unauthenticated rate budget.
        RecurringJob.AddOrUpdate<StepPackageCatalogPollJob>(
            "kraken.step-package-catalog-poll",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly(),
            new RecurringJobOptions { TimeZone = utc });

        // Subscription poller (M13.B.2/3) — every minute. Reads
        // audit_entries since the cursor in subscription_poller_state,
        // matches active subscriptions, dispatches transport deliveries.
        // Latency floor for an event reaching a webhook is therefore
        // ~1 minute. Idempotency (UNIQUE subscription+event on the
        // delivery table) lets the job re-run safely on crash recovery.
        // Disable temporarily by removing this recurring entry from
        // /hangfire's recurring-jobs view; per-subscription pause goes
        // through the Disabled flag on the row.
        RecurringJob.AddOrUpdate<SubscriptionPollerJob>(
            SubscriptionPollerJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = utc });
    }
}

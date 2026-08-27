using Hangfire;
using KrakenDeploy.ControlPlane.Provisioning;
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

        // WP3 — auto-fail manual-intervention gates nobody answered before their
        // expiry — every minute. Minutely because a paused task HOLDS its
        // (project, environment, tenant) slot, so a stale gate blocks that project's
        // whole environment until it is cleared.
        RecurringJob.AddOrUpdate<InterruptionTimeoutJob>(
            "kraken.interruption-timeout",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = utc });

        // Refresh the community step-template catalog from the configured
        // feeds (SC6 multi-feed; defaults: OctopusDeploy/Library + the
        // Kraken community repo) — hourly. Uses the Git Trees API (single
        // request per feed) + raw URLs (off-limit), so hourly polling stays
        // comfortably within GitHub's 60-req/hour unauthenticated rate
        // budget. Gated on feeds.step-template-catalog inside the job.
        RecurringJob.AddOrUpdate<StepTemplateCatalogPollJob>(
            "kraken.step-template-catalog-poll",
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Hourly(),
            new RecurringJobOptions { TimeZone = utc });

        // Step-package catalog (Phase D-9) — defaults to
        // DomagojJugovich/kraken-steps (SC6; the old KrakenDeploy default was
        // a squatted GitHub name), configurable via StepPackages:Catalog:Owner
        // / .Repo. Uses the same kraken.github named HttpClient + optional
        // GitHub:Token as the step-template catalog above; one /releases call
        // per hour is cheap even on the unauthenticated rate budget. Gated on
        // feeds.step-package-catalog inside the job.
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

        // Email-digest flusher (M13.B.2/3 phase 5) — every minute.
        // Drains email_digest_outbox: one digest email per subscription
        // whose configured DigestEveryMinutes window has elapsed. Cap of
        // 100 events per digest (Octopus parity); remainder waits for
        // the next cycle.
        RecurringJob.AddOrUpdate<EmailDigestFlushJob>(
            EmailDigestFlushJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = utc });

        // WP9 — scheduled retention sweep: walks every Space and applies the
        // full retention policy (deployments, releases, reference-protected
        // packages, runbook runs, aged step logs, orphaned live logs) plus the
        // on-disk artifact / drop-bundle cleanup the row prune cannot do. 03:30
        // UTC daily so it follows the audit + AI-call-log purges. Ships behind
        // the retention.sweep-dry-run flag (default ON) — dry-run logs the prune
        // set and deletes nothing until an operator flips it off.
        RecurringJob.AddOrUpdate<RetentionSweepJob>(
            RetentionSweepJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            Cron.Daily(3, 30),
            new RecurringJobOptions { TimeZone = utc });
    }

    /// <summary>
    /// Multi-account (SaaS) variant: registers the SAME recurring-job ids but with a
    /// per-account fan-out body (<see cref="PerAccountRecurringJobRunner"/>), so each
    /// per-tenant job runs once per active account inside a <c>WithAccount</c> scope.
    /// Re-using the ids means <c>AddOrUpdate</c> REPLACES any stale single-tenant
    /// schedule persisted by an earlier single-tenant run — so they stop firing
    /// without an account. Call this instead of <see cref="RegisterRecurringJobs"/>
    /// when <c>Deployment:Topology</c> is <c>Saas</c>.
    /// </summary>
    public static void RegisterPerAccountRecurringJobs()
    {
        var utc = TimeZoneInfo.Utc;

        void Fanout<TJob>(string id, string cron) =>
            RecurringJob.AddOrUpdate<PerAccountRecurringJobRunner>(
                id,
                runner => runner.RunForAllAccountsAsync(
                    typeof(TJob).AssemblyQualifiedName!, CancellationToken.None),
                cron,
                new RecurringJobOptions { TimeZone = utc });

        Fanout<AuditRetentionJob>("kraken.audit-retention", Cron.Daily(3, 0));
        Fanout<AiCallLogRetentionJob>("kraken.ai-call-log-retention", Cron.Daily(3, 15));
        Fanout<AgentLastSeenOfflineJob>("kraken.agent-last-seen-offline", "*/5 * * * *");
        Fanout<RegistrationTokenExpiryJob>("kraken.registration-token-expiry", Cron.Daily(2, 0));
        Fanout<ScheduledDeploymentDispatchJob>("kraken.scheduled-deployment-dispatch", Cron.Minutely());
        Fanout<InterruptionTimeoutJob>("kraken.interruption-timeout", Cron.Minutely());
        Fanout<StepTemplateCatalogPollJob>("kraken.step-template-catalog-poll", Cron.Hourly());
        Fanout<StepPackageCatalogPollJob>("kraken.step-package-catalog-poll", Cron.Hourly());
        Fanout<SubscriptionPollerJob>(SubscriptionPollerJob.RecurringJobId, Cron.Minutely());
        Fanout<EmailDigestFlushJob>(EmailDigestFlushJob.RecurringJobId, Cron.Minutely());
        Fanout<RetentionSweepJob>(RetentionSweepJob.RecurringJobId, Cron.Daily(3, 30));
    }

    /// <summary>
    /// Blue-green drain-watcher — PLATFORM-global, never a per-account fan-out:
    /// it reads the release registry and probes slot instances over HTTP (no
    /// tenant DB). Retires a Draining release once its slots report zero circuits
    /// + zero in-flight work (§5/§9 of the design). Every slot instance registers
    /// this same id against the shared Hangfire storage, so it runs once per
    /// minute fleet-wide regardless of which instance picks it up. Registered
    /// under both blue-green topologies (BG1 item 4) IN ADDITION to the
    /// topology's regular job set.
    /// </summary>
    public static void RegisterReleaseDrainWatch()
    {
        RecurringJob.AddOrUpdate<KrakenDeploy.Platform.Releases.ReleaseDrainWatcher>(
            "kraken.release-drain-watch",
            watcher => watcher.ExecuteAsync(CancellationToken.None),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}

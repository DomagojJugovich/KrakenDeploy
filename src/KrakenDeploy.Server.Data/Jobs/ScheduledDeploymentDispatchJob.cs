using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job (minutely) AND boot-time reconciler for dispatch —
/// B1 durable dispatch. The DB row is the source of truth; the in-process
/// channels are wake-up signals; the worker's atomic claim
/// (<see cref="ServerTaskLease.TryClaimAsync"/>) makes execution exactly-once.
/// This job is the at-least-once signaller + orphan recovery, in three steps:
/// <list type="number">
///   <item><b>Due scheduled deployments</b> — <c>Queued</c> with an arrived
///   <c>ScheduledFor</c> → wake-up. Pure enqueue, no state change (the claim
///   clears <c>ScheduledFor</c>); a crash mid-job strands nothing.</item>
///   <item><b>Stale Queued tasks</b> (both kinds) — <c>Queued</c>,
///   <c>ScheduledFor == null</c>, older than a short grace: their create-time
///   wake-up died with the channel (restart) or was never consumed → re-signal
///   to the right channel per <see cref="ServerTaskKind"/>.</item>
///   <item><b>Orphaned Running deployments</b> — lease expired (or never
///   stamped, e.g. rows from before this feature): the owning process is dead
///   and its in-memory orchestration state (waves, sub-plan TCS) is
///   unresumable → conditional flip to <c>Failed</c> + a
///   <c>Deployment.Interrupted</c> audit row. A LIVE lease is never touched —
///   that is what keeps a draining blue-green slot's runs safe — and runbook
///   runs are excluded entirely (after dispatch they are agent-owned; the hub
///   writes their terminal status even across a server restart).</item>
/// </list>
/// The same <see cref="ExecuteAsync"/> body runs once at startup (before the
/// workers begin consuming) and every minute thereafter, so recovery does not
/// depend on a restart. Registered per-account via the fan-out in multi-account
/// mode.
/// </summary>
public sealed class ScheduledDeploymentDispatchJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    Channel<TenantWorkItem> deploymentQueue,
    RunbookRunChannel runbookQueue,
    TimeProvider time,
    IAccountContext accountContext,
    IAuditLog auditLog,
    ILogger<ScheduledDeploymentDispatchJob> logger)
{
    /// <summary>How long a fresh Queued row is left alone before it is treated
    /// as a lost wake-up. Its create-time channel item is normally consumed
    /// within milliseconds; the grace only avoids redundant signalling while a
    /// busy worker drains its backlog (duplicates would be harmless — the claim
    /// eats them — just noisy).</summary>
    internal static readonly TimeSpan StaleQueuedGrace = TimeSpan.FromMinutes(2);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = time.GetUtcNow();
        // Runs inside the per-account fan-out (WithAccount) in multi-account mode,
        // so CurrentAccountId is this account; Guid.Empty in single-instance mode.
        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;

        await SignalDueScheduledAsync(db, now, accountId, ct).ConfigureAwait(false);
        await SignalStaleQueuedAsync(db, now, accountId, ct).ConfigureAwait(false);
        await ReconcileOrphanedRunningAsync(db, now, ct).ConfigureAwait(false);
    }

    // ── 1. Due scheduled deployments ─────────────────────────────────────────

    private async Task SignalDueScheduledAsync(
        KrakenDbContext db, DateTimeOffset now, Guid accountId, CancellationToken ct)
    {
        // Deployment-kind only — runbook triggers never set ScheduledFor.
        // IgnoreQueryFilters: dispatch is space-agnostic.
        var dueIds = await db.Deployments
            .IgnoreQueryFilters()
            .Where(d => d.Status == DeploymentStatus.Queued
                     && d.ScheduledFor != null
                     && d.ScheduledFor <= now)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var id in dueIds)
        {
            await deploymentQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, id), ct)
                .ConfigureAwait(false);
        }

        if (dueIds.Count > 0)
        {
            logger.LogInformation(
                "ScheduledDeploymentDispatch: signalled {Count} due scheduled deployment(s).",
                dueIds.Count);
        }
    }

    // ── 2. Stale Queued re-signal (both kinds) ───────────────────────────────

    private async Task SignalStaleQueuedAsync(
        KrakenDbContext db, DateTimeOffset now, Guid accountId, CancellationToken ct)
    {
        var staleBefore = now - StaleQueuedGrace;
        var stale = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Status == DeploymentStatus.Queued
                     && t.ScheduledFor == null
                     && t.CreatedUtc < staleBefore)
            .Select(t => new { t.Id, t.Kind })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var task in stale)
        {
            var item = new TenantWorkItem(accountId, task.Id);
            if (task.Kind == ServerTaskKind.RunbookRun)
            {
                await runbookQueue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
            }
            else
            {
                await deploymentQueue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
            }
        }

        if (stale.Count > 0)
        {
            logger.LogWarning(
                "Dispatch reconcile: re-signalled {Count} stale Queued task(s) whose original " +
                "wake-up was lost (server restart or dropped channel write).",
                stale.Count);
        }
    }

    // ── 3. Orphaned Running deployments ──────────────────────────────────────

    private async Task ReconcileOrphanedRunningAsync(
        KrakenDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Candidates: Running DEPLOYMENTS whose lease expired or was never
        // stamped. Runbook runs are structurally excluded (db.Deployments is the
        // TPH subtype set): once dispatched they are agent-owned and the hub
        // finalises them across restarts.
        var orphans = await db.Deployments
            .IgnoreQueryFilters()
            .Where(d => d.Status == DeploymentStatus.Running
                     && (d.LeaseUntil == null || d.LeaseUntil < now))
            .Select(d => new { d.Id, d.ClaimedBy, d.LeaseUntil })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var orphan in orphans)
        {
            // Conditional flip — the WHERE re-checks status AND lease so a run
            // whose owner renewed (or finished) between the SELECT and this
            // UPDATE is left alone. Fail-closed against killing live work.
            var rows = await db.Deployments
                .IgnoreQueryFilters()
                .Where(d => d.Id == orphan.Id
                         && d.Status == DeploymentStatus.Running
                         && (d.LeaseUntil == null || d.LeaseUntil < now))
                .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Status, DeploymentStatus.Failed)
                        .SetProperty(d => d.CompletedUtc, now)
                        .SetProperty(d => d.ClaimedBy, (string?)null)
                        .SetProperty(d => d.LeaseUntil, (DateTimeOffset?)null),
                    ct)
                .ConfigureAwait(false);
            if (rows == 0)
            {
                continue;
            }

            logger.LogWarning(
                "Dispatch reconcile: deployment {Id} was Running with an expired/absent lease " +
                "(owner {Owner}, lease {Lease}) — its orchestrating process died; marked Failed.",
                orphan.Id, orphan.ClaimedBy ?? "<none>", orphan.LeaseUntil);

            // ExecuteUpdate bypasses the audit interceptor — record explicitly.
            await auditLog.RecordAsync(
                AuditEventType.DeploymentInterrupted,
                subjectType: "Deployment",
                subjectId:   orphan.Id.ToString(),
                details:     $"Interrupted by server crash/restart: the dispatch lease " +
                             $"(owner {orphan.ClaimedBy ?? "<none>"}, expiry " +
                             $"{orphan.LeaseUntil?.ToString("O") ?? "<never stamped>"}) was not " +
                             "renewed. In-memory orchestration state is unresumable; marked Failed.",
                ct:          ct).ConfigureAwait(false);
        }
    }
}

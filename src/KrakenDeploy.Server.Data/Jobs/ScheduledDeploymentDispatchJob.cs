using System.Linq.Expressions;
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
/// channel is a wake-up signal; the worker's atomic claim
/// (<see cref="ServerTaskLease.TryClaimAsync"/>) makes execution exactly-once.
/// This job is the at-least-once signaller + orphan recovery. Since the D1
/// engine merge BOTH kinds share the unified orchestrator, so the arms are
/// kind-agnostic:
/// <list type="number">
///   <item><b>Due scheduled tasks</b> (both kinds) — <c>Queued</c> with an
///   arrived <c>ScheduledFor</c> → wake-up. Pure enqueue, no state change (the
///   claim clears <c>ScheduledFor</c>); a crash mid-job strands nothing.</item>
///   <item><b>Stale Queued tasks</b> (both kinds) — <c>Queued</c>,
///   <c>ScheduledFor == null</c>, older than a short grace: their create-time
///   wake-up died with the channel (restart) or was never consumed → re-signal
///   to the shared task channel.</item>
///   <item><b>Resolved paused tasks</b> (WP3, both kinds) — <c>Paused</c> at a
///   manual-intervention gate that has since been answered → re-signal. The pause
///   path's equivalent of arm 2: a paused task has no lease and is invisible to arm
///   4, so without this arm a resume wake-up lost to a restart would strand the task
///   forever — and a restart inside a multi-day approval window is likely.</item>
///   <item><b>Orphaned Running tasks</b> (both kinds) — a Running task whose
///   orchestrating process died: an EXPIRED lease, or a NULL lease (a Running
///   row that never got one — nothing owns it) → conditional flip to
///   <c>Failed</c> + a kind-appropriate <c>*.Interrupted</c> audit row; a LIVE
///   lease is never touched (keeps a draining blue-green slot's runs safe).
///   D1 Phase 3 removed the transition-era kind branch: both kinds now hold a
///   live lease for the whole orchestration (the pre-D1 hand-off model — and
///   arm 4, its <c>Engine:MaxRunbookRunDuration</c> drain ceiling — is gone).</item>
/// </list>
/// The same <see cref="ExecuteAsync"/> body runs once at startup (before the
/// workers begin consuming) and every minute thereafter, so recovery does not
/// depend on a restart. Registered per-account via the fan-out in multi-account
/// mode.
/// </summary>
public sealed class ScheduledDeploymentDispatchJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    Channel<TenantWorkItem> taskQueue,
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
        await SignalResolvedPausedAsync(db, accountId, ct).ConfigureAwait(false);
        await ReconcileOrphanedRunningAsync(db, now, ct).ConfigureAwait(false);
    }

    // ── 1. Due scheduled tasks (both kinds) ──────────────────────────────────

    private async Task SignalDueScheduledAsync(
        KrakenDbContext db, DateTimeOffset now, Guid accountId, CancellationToken ct)
    {
        // D1: both kinds may carry a future ScheduledFor (deployments today;
        // runbook runs once the Phase-2 trigger surface sets it). The unified
        // worker branches on Kind when it dequeues, so a single wake-up onto the
        // shared channel dispatches either kind. IgnoreQueryFilters: dispatch is
        // space-agnostic.
        var dueIds = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Status == DeploymentStatus.Queued
                     && t.ScheduledFor != null
                     && t.ScheduledFor <= now)
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var id in dueIds)
        {
            await taskQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, id), ct)
                .ConfigureAwait(false);
        }

        if (dueIds.Count > 0)
        {
            logger.LogInformation(
                "ScheduledDeploymentDispatch: signalled {Count} due scheduled task(s).",
                dueIds.Count);
        }
    }

    // ── 2. Stale Queued re-signal (both kinds) ───────────────────────────────

    private async Task SignalStaleQueuedAsync(
        KrakenDbContext db, DateTimeOffset now, Guid accountId, CancellationToken ct)
    {
        var staleBefore = now - StaleQueuedGrace;
        var staleIds = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Status == DeploymentStatus.Queued
                     && t.ScheduledFor == null
                     && t.CreatedUtc < staleBefore)
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // D1: one shared channel for both kinds — no per-kind channel branch.
        foreach (var id in staleIds)
        {
            await taskQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, id), ct)
                .ConfigureAwait(false);
        }

        if (staleIds.Count > 0)
        {
            logger.LogWarning(
                "Dispatch reconcile: re-signalled {Count} stale Queued task(s) whose original " +
                "wake-up was lost (server restart or dropped channel write).",
                staleIds.Count);
        }
    }

    // ── 3. Resolved manual-intervention gates (WP3, both kinds) ──────────────

    private async Task SignalResolvedPausedAsync(
        KrakenDbContext db, Guid accountId, CancellationToken ct)
    {
        // A task parked at an approval gate is Paused with NO lease, so arm 4 below
        // cannot see it (its predicate is scoped to Running) — which is exactly the
        // exemption that keeps a 72-hour approval window from being reaped as an
        // orphan. The flip side is that nothing else would ever pick the task up
        // again if its resume wake-up were lost: the approve/reject handler enqueues
        // one, but that channel item dies with a server restart, and a restart is
        // LIKELY inside a multi-day window.
        //
        // So this arm is the pause path's equivalent of the stale-Queued re-signal:
        // any Paused task whose gate is already answered gets re-signalled. Read-only
        // then enqueue, so it is crash-safe and idempotent — the conditional
        // Paused→Running resume (ServerTaskLease.TryResumeAsync) makes duplicate
        // wake-ups harmless. No grace period: unlike a fresh Queued row, a resolved
        // gate has no in-flight wake-up we would be racing, and a paused deployment
        // holds its (project, environment, tenant) slot while it waits.
        // Two flat EXISTS subqueries rather than one nested pair: "has a gate at all"
        // AND "has no gate still Pending". The first is not redundant — it excludes a
        // Paused row with zero interruptions, which the pause path cannot produce (the
        // gate row and the status flip share one transaction) but which would otherwise
        // be re-signalled every minute forever, pausing itself again each time.
        // The inner IgnoreQueryFilters calls ARE redundant with the root one (the flag
        // is per-statement in EF Core 10 — ToQueryString-verified: it unfilters
        // navigations and subqueries too). Kept so each subquery stays correct if it is
        // ever split into its own query.
        var resumableIds = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Status == DeploymentStatus.Paused
                     && db.Interruptions.IgnoreQueryFilters()
                         .Any(i => i.TaskId == t.Id)
                     && !db.Interruptions.IgnoreQueryFilters()
                         .Any(i => i.TaskId == t.Id
                                && i.Status == InterruptionStatus.Pending))
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var id in resumableIds)
        {
            await taskQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, id), ct)
                .ConfigureAwait(false);
        }

        if (resumableIds.Count > 0)
        {
            logger.LogInformation(
                "Dispatch reconcile: re-signalled {Count} paused task(s) whose " +
                "manual-intervention gate has been answered.",
                resumableIds.Count);
        }
    }

    // ── 4. Orphaned Running tasks (both kinds) ───────────────────────────────

    private async Task ReconcileOrphanedRunningAsync(
        KrakenDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Candidates: Running tasks whose orchestrating process died. Both kinds
        // hold (and renew) a live lease for the whole orchestration, so an
        // EXPIRED lease means the owner died and the in-memory wave/sub-plan
        // state is unresumable, and a NULL lease means the Running row never got
        // an owner at all (pre-B1 rows; interrupted claims) — fail either,
        // regardless of kind. (D1 Phase 3: the transition-era kind branch that
        // spared null-lease runbook runs for the legacy hand-off drain is gone.)
        var orphans = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(OrphanedRunningPredicate(now))
            .Select(t => new { t.Id, t.Kind, t.ClaimedBy, t.LeaseUntil })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var orphan in orphans)
        {
            // Conditional flip — re-apply the SAME predicate (shared factory, so the
            // re-check can never drift from the candidate SELECT) plus the id, so a
            // task whose owner renewed (or finished) between the SELECT and this
            // UPDATE is left alone. Fail-closed against killing live work.
            var rows = await db.ServerTasks
                .IgnoreQueryFilters()
                .Where(OrphanedRunningPredicate(now))
                .Where(t => t.Id == orphan.Id)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Status, DeploymentStatus.Failed)
                        .SetProperty(t => t.CompletedUtc, now)
                        .SetProperty(t => t.ClaimedBy, (string?)null)
                        .SetProperty(t => t.LeaseUntil, (DateTimeOffset?)null),
                    ct)
                .ConfigureAwait(false);
            if (rows == 0)
            {
                continue;
            }

            // Additive audit vocabulary: never rename Deployment.Interrupted.
            var (eventType, subjectType) = orphan.Kind == ServerTaskKind.RunbookRun
                ? (AuditEventType.RunbookRunInterrupted, "RunbookRun")
                : (AuditEventType.DeploymentInterrupted, "Deployment");

            // "expired/absent lease" — the predicate reaps an EXPIRED (non-null)
            // lease AND a null (never-stamped) lease for either kind, so {Lease}
            // may render empty for the latter.
            logger.LogWarning(
                "Dispatch reconcile: {Kind} {Id} was Running with an expired/absent lease " +
                "(owner {Owner}, lease {Lease}) — its orchestrating process died; marked Failed.",
                orphan.Kind, orphan.Id, orphan.ClaimedBy ?? "<none>", orphan.LeaseUntil);

            // ExecuteUpdate bypasses the audit interceptor — record explicitly.
            await auditLog.RecordAsync(
                eventType,
                subjectType: subjectType,
                subjectId:   orphan.Id.ToString(),
                details:     $"Interrupted by server crash/restart: the dispatch lease " +
                             $"(owner {orphan.ClaimedBy ?? "<none>"}, expiry " +
                             $"{orphan.LeaseUntil?.ToString("O") ?? "<never stamped>"}) was not " +
                             "renewed. In-memory orchestration state is unresumable; marked Failed.",
                ct:          ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Single source for the orphaned-Running predicate so the candidate SELECT and
    /// the conditional-flip UPDATE re-check can never drift (they MUST match for the
    /// optimistic re-check to be sound). An EXPIRED (non-null) lease and a NULL
    /// (never-stamped) lease are both orphaned, for EITHER kind — every live
    /// orchestration holds a lease for its whole duration (D1 Phase 3 removed the
    /// transition-era null-lease runbook-run exemption).
    /// </summary>
    private static Expression<Func<ServerTask, bool>> OrphanedRunningPredicate(DateTimeOffset now)
        => t => t.Status == DeploymentStatus.Running
             && (t.LeaseUntil < now || t.LeaseUntil == null);
}

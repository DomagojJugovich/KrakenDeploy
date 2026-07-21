using System.Linq.Expressions;
using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job (minutely) AND boot-time reconciler for dispatch —
/// B1 durable dispatch. The DB row is the source of truth; the in-process
/// channel is a wake-up signal; the worker's atomic claim
/// (<see cref="ServerTaskLease.TryClaimAsync"/>) makes execution exactly-once.
/// This job is the at-least-once signaller + orphan recovery. Since the D1
/// engine merge BOTH kinds share the unified orchestrator, so the arms are
/// (mostly) kind-agnostic:
/// <list type="number">
///   <item><b>Due scheduled tasks</b> (both kinds) — <c>Queued</c> with an
///   arrived <c>ScheduledFor</c> → wake-up. Pure enqueue, no state change (the
///   claim clears <c>ScheduledFor</c>); a crash mid-job strands nothing.</item>
///   <item><b>Stale Queued tasks</b> (both kinds) — <c>Queued</c>,
///   <c>ScheduledFor == null</c>, older than a short grace: their create-time
///   wake-up died with the channel (restart) or was never consumed → re-signal
///   to the shared task channel.</item>
///   <item><b>Orphaned Running tasks</b> — a Running task whose orchestrating
///   process died. An EXPIRED lease (either kind) → conditional flip to
///   <c>Failed</c> + a kind-appropriate <c>*.Interrupted</c> audit row; a LIVE
///   lease is never touched (keeps a draining blue-green slot's runs safe). The
///   NULL-lease case is KIND-BRANCHED: a null-lease Running DEPLOYMENT is a
///   genuine pre-B1 orphan (failed), but a null-lease Running RUNBOOK RUN is a
///   LEGACY hand-off run (agent-owned, hub-finalised) that arm 4 drains by the
///   ceiling for one release — applying "null OR expired" to runbook runs would
///   kill legacy hand-off runs at boot.</item>
///   <item><b>Legacy runbook-run ceiling (INTERIM — DELETE after one release)</b>
///   — a pre-D1 runbook run handed off with a RELEASED lease (<c>LeaseUntil
///   == null</c>) is finalised by the hub on the agent's completion callback. If
///   that agent died nothing else finalises it, so a run older than
///   <c>Engine:MaxRunbookRunDuration</c> is failed. Post-D1 runbook runs hold a
///   live lease for the whole run and are covered by arm 3 + B3, so this arm only
///   exists to drain runs that were in flight ACROSS the D1 upgrade. Remove it
///   (and <c>Engine:MaxRunbookRunDuration</c>) one release after D1 ships.</item>
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
    IOptions<EngineOptions> engineOptions,
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
        await ReconcileLegacyRunbookCeilingAsync(db, now, ct).ConfigureAwait(false);
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

    // ── 3. Orphaned Running tasks (both kinds) ───────────────────────────────

    private async Task ReconcileOrphanedRunningAsync(
        KrakenDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Candidates: Running tasks whose orchestrating process died. D1: both
        // kinds now hold (and renew) a live lease for the whole orchestration, so
        // an EXPIRED lease (LeaseUntil < now — SQL excludes nulls) means the owner
        // died and the in-memory wave/sub-plan state is unresumable → fail it,
        // regardless of kind.
        //
        // The NULL-lease case is KIND-BRANCHED (the two-release trap): a null-lease
        // Running DEPLOYMENT is a genuine pre-B1 orphan (fail it), but a null-lease
        // Running RUNBOOK RUN is a LEGACY run handed off by the pre-D1 worker
        // (agent-owned, hub-finalised) — arm 4 drains those by the ceiling for one
        // release. Applying the deployment's "null OR expired" to runbook runs
        // would kill legacy hand-off runs at boot.
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
            // lease for either kind AND a null (never-stamped) lease for a
            // pre-B1 deployment, so {Lease} may render empty for the latter.
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
    /// optimistic re-check to be sound). An EXPIRED (non-null) lease is orphaned for
    /// EITHER kind; a NULL (never-stamped) lease is a pre-B1 orphan ONLY for a
    /// deployment — a null-lease runbook run is a legacy hand-off drained by arm 4.
    /// </summary>
    private static Expression<Func<ServerTask, bool>> OrphanedRunningPredicate(DateTimeOffset now)
        => t => t.Status == DeploymentStatus.Running
             && (t.LeaseUntil < now
                 || (t.LeaseUntil == null && t.Kind == ServerTaskKind.Deployment));

    // ── 4. Legacy runbook-run ceiling (INTERIM — DELETE after one release) ────

    /// <summary>
    /// D1 transition arm: a runbook run handed off by the PRE-D1 worker released
    /// its lease at hand-off (<c>LeaseUntil == null</c>) and was finalised by the
    /// hub on the agent's completion callback. If that agent died nothing else can
    /// finalise the row, so a run older than <c>Engine:MaxRunbookRunDuration</c>
    /// is failed. Post-D1 runbook runs hold a live lease for the whole run (arm 3
    /// covers a dead orchestrator; B3 covers a dead agent), so this arm ONLY
    /// drains runs that were in flight ACROSS the D1 upgrade. DELETE this arm and
    /// <c>Engine:MaxRunbookRunDuration</c> one release after D1 ships.
    /// <para>
    /// The pre-D1 <c>ReapDisconnectedRunbookRunsAsync</c> (E9 interim disconnect
    /// reap via <c>IAgentLivenessProbe</c>) and the pre-hand-off expired-lease arm
    /// are GONE: post-D1 runbook runs go through B3's wave-level disconnect monitor
    /// and arm 3's lease reconcile like deployments.
    /// </para>
    /// </summary>
    private async Task ReconcileLegacyRunbookCeilingAsync(
        KrakenDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var ceiling = engineOptions.Value.MaxRunbookRunDuration;
        if (ceiling <= TimeSpan.Zero)
        {
            // Non-positive config would reap everything instantly — keep a
            // ceiling rather than reintroducing the unbounded hang.
            ceiling = TimeSpan.FromHours(1);
        }
        var overdueBefore = now - ceiling;

        var overdue = await db.RunbookRuns
            .IgnoreQueryFilters()
            .Where(r => r.Status == DeploymentStatus.Running
                     && r.LeaseUntil == null
                     && r.StartedUtc != null && r.StartedUtc < overdueBefore)
            .Select(r => new { r.Id, r.StartedUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var run in overdue)
        {
            var rows = await db.RunbookRuns
                .IgnoreQueryFilters()
                .Where(r => r.Id == run.Id
                         && r.Status == DeploymentStatus.Running
                         && r.LeaseUntil == null)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, DeploymentStatus.Failed)
                        .SetProperty(r => r.CompletedUtc, now),
                    ct)
                .ConfigureAwait(false);
            if (rows == 0)
            {
                continue;
            }

            logger.LogWarning(
                "Dispatch reconcile: legacy runbook run {Id} started {Started} and never reported " +
                "completion within {Ceiling} — marked Failed (pre-D1 hand-off run).",
                run.Id, run.StartedUtc, ceiling);

            await auditLog.RecordAsync(
                AuditEventType.RunbookRunTimedOut,
                subjectType: "RunbookRun",
                subjectId:   run.Id.ToString(),
                details:     $"Agent never reported completion: started {run.StartedUtc:O}, " +
                             $"exceeded Engine:MaxRunbookRunDuration ({ceiling}). Marked Failed. " +
                             "A late agent completion will be ignored (terminal-status guard). " +
                             "Legacy pre-D1 hand-off run — this arm is removed one release after D1.",
                ct:          ct).ConfigureAwait(false);
        }
    }
}

using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
///   <item><b>Overdue runbook runs (B3)</b> — the runbook analogue step 3
///   deliberately skips: a <c>Running</c> run with an EXPIRED lease died
///   between claim and agent hand-off (the plan never reached the agent);
///   a <c>Running</c> run with a RELEASED lease is agent-owned, and one whose
///   <c>StartedUtc</c> exceeds <c>Engine:MaxRunbookRunDuration</c> never got
///   its completion callback — nothing else can ever finalize either, so both
///   flip to <c>Failed</c> with their own audit events.</item>
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
    IOptions<EngineOptions> engineOptions,
    IAgentLivenessProbe livenessProbe,
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
        await ReconcileOverdueRunbookRunsAsync(db, now, ct).ConfigureAwait(false);
        await ReapDisconnectedRunbookRunsAsync(db, now, ct).ConfigureAwait(false);
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

    // ── 4. Overdue runbook runs (B3) ─────────────────────────────────────────

    private async Task ReconcileOverdueRunbookRunsAsync(
        KrakenDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // 4a — dispatch died PRE-hand-off: Running with an EXPIRED lease. The
        // worker holds (and renews) the lease only between the atomic claim and
        // the RunDeploymentAsync push; an expired lease means that process died
        // and the plan never reached the agent. Step 3 deliberately excludes
        // runbook runs because a RELEASED lease is their normal agent-owned
        // state — this is their equivalent for the pre-hand-off window.
        var preHandoffOrphans = await db.RunbookRuns
            .IgnoreQueryFilters()
            .Where(r => r.Status == DeploymentStatus.Running
                     && r.LeaseUntil != null && r.LeaseUntil < now)
            .Select(r => new { r.Id, r.ClaimedBy, r.LeaseUntil })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var orphan in preHandoffOrphans)
        {
            // Conditional flip — fail-closed against racing a live owner that
            // renewed or handed off between the SELECT and this UPDATE.
            var rows = await db.RunbookRuns
                .IgnoreQueryFilters()
                .Where(r => r.Id == orphan.Id
                         && r.Status == DeploymentStatus.Running
                         && r.LeaseUntil != null && r.LeaseUntil < now)
                .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, DeploymentStatus.Failed)
                        .SetProperty(r => r.CompletedUtc, now)
                        .SetProperty(r => r.ClaimedBy, (string?)null)
                        .SetProperty(r => r.LeaseUntil, (DateTimeOffset?)null),
                    ct)
                .ConfigureAwait(false);
            if (rows == 0)
            {
                continue;
            }

            logger.LogWarning(
                "Dispatch reconcile: runbook run {Id} was Running with an expired lease " +
                "(owner {Owner}, lease {Lease}) — the dispatching process died before the " +
                "agent hand-off; marked Failed.",
                orphan.Id, orphan.ClaimedBy ?? "<none>", orphan.LeaseUntil);

            await auditLog.RecordAsync(
                AuditEventType.RunbookRunInterrupted,
                subjectType: "RunbookRun",
                subjectId:   orphan.Id.ToString(),
                details:     $"Interrupted by server crash/restart: the dispatch lease " +
                             $"(owner {orphan.ClaimedBy ?? "<none>"}, expiry " +
                             $"{orphan.LeaseUntil?.ToString("O") ?? "<never stamped>"}) expired " +
                             "before the agent hand-off; the plan never reached the agent. " +
                             "Marked Failed.",
                ct:          ct).ConfigureAwait(false);
        }

        // 4b — agent-owned run never finished: the lease is RELEASED at
        // hand-off and the hub finalizes on the agent's completion callback.
        // If the agent died (or lost the run) nothing else can ever finalize
        // the row, so a run older than Engine:MaxRunbookRunDuration is failed.
        // The B2 agent buffers and re-sends completions across reconnects, so
        // a run still genuinely in flight is finalized by the flush long before
        // a sane ceiling; a late completion AFTER this reap is swallowed by the
        // hub's IsTerminal guard.
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
                "Dispatch reconcile: runbook run {Id} started {Started} and never reported " +
                "completion within {Ceiling} — marked Failed.",
                run.Id, run.StartedUtc, ceiling);

            await auditLog.RecordAsync(
                AuditEventType.RunbookRunTimedOut,
                subjectType: "RunbookRun",
                subjectId:   run.Id.ToString(),
                details:     $"Agent never reported completion: started {run.StartedUtc:O}, " +
                             $"exceeded Engine:MaxRunbookRunDuration ({ceiling}). Marked Failed. " +
                             "A late agent completion will be ignored (terminal-status guard).",
                ct:          ct).ConfigureAwait(false);
        }
    }

    // ── 4c. Disconnect-aware runbook reap (E9 — INTERIM) ─────────────────────

    /// <summary>
    /// E9 (INTERIM — superseded by the D1 engine merge, after which B3's
    /// wave-level disconnect monitor applies to runbook runs too; DELETE THIS
    /// then). Runbook runs bypass the wave machinery (no sub-plan slot, lease
    /// released at hand-off), so the B3 disconnect monitor never engages: a
    /// killed agent leaves the run <c>Running</c> until the
    /// <c>Engine:MaxRunbookRunDuration</c> ceiling (default 1 h). This fails an
    /// agent-owned run whose single assigned target has been continuously
    /// disconnected past <see cref="EngineOptions.AgentDisconnectWaveGrace"/>
    /// instead — the same grace the deployment monitor uses.
    /// <para>
    /// "Continuously disconnected" combines two signals, fail-closed (both must
    /// agree before reaping live-looking work): the target's
    /// <c>LastSeenUtc</c> heartbeat is older than the grace (the scale-out-safe,
    /// shared-DB signal — a live agent heartbeats every 30 s) AND the node-local
    /// connection registry has no live tunnel for it right now
    /// (<see cref="IAgentLivenessProbe"/>). A target the registry still sees as
    /// connected (e.g. just reconnected, heartbeat not yet flushed) is left alone.
    /// </para>
    /// </summary>
    private async Task ReapDisconnectedRunbookRunsAsync(
        KrakenDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var grace = engineOptions.Value.AgentDisconnectWaveGrace;
        if (grace <= TimeSpan.Zero)
        {
            return; // disconnect monitor disabled (mirrors the wave monitor)
        }

        var disconnectedBefore = now - grace;

        // Agent-owned runbook runs (lease released at hand-off) still Running,
        // joined to their single assigned target's last heartbeat. The target set
        // is the authority (task_target_assignments); a runbook run has exactly one.
        var candidates = await (
            from r in db.RunbookRuns.IgnoreQueryFilters()
            where r.Status == DeploymentStatus.Running
               && r.LeaseUntil == null
               && r.StartedUtc != null
            join a in db.TaskTargetAssignments.IgnoreQueryFilters() on r.Id equals a.TaskId
            join t in db.DeploymentTargets.IgnoreQueryFilters() on a.TargetId equals t.Id
            select new { RunId = r.Id, a.TargetId, t.LastSeenUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var c in candidates)
        {
            // Heartbeat still fresh → the agent is alive, not disconnected.
            if (c.LastSeenUtc is { } seen && seen > disconnectedBefore)
            {
                continue;
            }
            // Registry still sees a live tunnel (reconnected on this node) → leave it.
            if (livenessProbe.IsTargetConnected(c.TargetId))
            {
                continue;
            }

            // Conditional flip — fail-closed against a live owner that reclaimed a
            // lease (hand-off retry) between the SELECT and this UPDATE.
            var rows = await db.RunbookRuns
                .IgnoreQueryFilters()
                .Where(r => r.Id == c.RunId
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
                "Dispatch reconcile: runbook run {Id}'s target {Target} has been " +
                "disconnected longer than {Grace} (last seen {LastSeen}); the agent died " +
                "mid-run — marked Failed.",
                c.RunId, c.TargetId, grace, c.LastSeenUtc);

            await auditLog.RecordAsync(
                AuditEventType.RunbookRunInterrupted,
                subjectType: "RunbookRun",
                subjectId:   c.RunId.ToString(),
                details:     $"Agent disconnected mid-run: target {c.TargetId} was continuously " +
                             $"disconnected past Engine:AgentDisconnectWaveGrace ({grace}), last seen " +
                             $"{c.LastSeenUtc?.ToString("O") ?? "<never>"}. Runbook runs bypass the B3 " +
                             "wave monitor, so the dispatch reconciler fails them. Marked Failed; a late " +
                             "agent completion will be ignored (terminal-status guard).",
                ct:          ct).ConfigureAwait(false);
        }
    }
}

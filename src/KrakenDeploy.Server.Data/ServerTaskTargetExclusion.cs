using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// F6 — server-side per-plan target exclusion, evaluated at claim time. No two
/// tasks operate on the same SERIAL target concurrently, for the whole plan
/// duration: per-script/per-wave mutexes (the agent's F5 reader-writer gate)
/// still interleave two PROCESSES at step boundaries, so the only real
/// protection for shared folders/sites/services is one plan at a time. Octopus
/// has no equivalent (Tentacle source verified 2026-07-25) — deliberate
/// divergence.
/// <para>
/// <b>Model (locked decisions P1–P7, 2026-07-25).</b> A task's mode on target
/// <c>T</c> is <c>Shared</c> when <c>T.AllowParallelTaskExecution</c> OR the
/// SOURCE consents (the project's flag for a deployment, the runbook's own flag
/// for a run) — the same OR the plan builder stamps into
/// <c>DeploymentPlan.AllowParallelTaskExecution</c>. Two tasks CONFLICT when
/// they share a target on which at least one side is Exclusive; algebraically
/// that reduces to: a shared target with <c>!T.Allow</c> where NOT both sources
/// consent. Mutual-Shared overlap neither defers nor orders. The in-flight rows
/// of <c>server_tasks</c> ARE the lock state — crash-release comes free from the
/// B1 lease/reconciler, and blue-green slots share one Postgres so the exclusion
/// spans processes.
/// </para>
/// <para>
/// <b>Who participates.</b> Deployments AND runbook runs, fully symmetric
/// (C4 — the F1 (project,env,tenant) exemption for runbook runs does NOT extend
/// here). Ad-hoc scripts are not <c>server_tasks</c> and stay invisible to this
/// predicate (accepted wave-gap residual); they take the READ side of the agent
/// gate instead. The unit of exclusion is the <c>DeploymentTarget</c> ROW, not
/// the physical machine: one box registered as two targets (nothing enforces
/// <c>MachineName</c> uniqueness) is two identities that never conflict — the
/// same pre-existing aliasing residual the agent gate documents in
/// <c>node-concurrency-and-cache.md</c>.
/// </para>
/// <para>
/// <b>CHILD tasks are fully EXEMPT</b> (<c>ParentTaskId != null</c> — spawned by
/// an <c>Octopus.DeployRelease</c> step): the caller skips this predicate for
/// them entirely, so a child is never <c>TargetBlocked</c>. A child is the
/// continuation of a parent that already claimed those targets, so its work is
/// ALREADY accounted for by the parent's hold — the same reasoning behind the
/// maintenance-gate and E3 <c>NodeTaskGate</c> child bypasses. Exempting only
/// part of the predicate is not enough: deferring a child to ANY non-ancestor
/// task deadlocks whenever that task is (directly or transitively) waiting on
/// the child's own parent — two mutually-consenting parents on one box, each
/// awaiting a non-consenting child, is the reachable case — and the parent's
/// child-wait budget does not bound it (<c>DeployReleaseStepRunner</c> charges
/// its working budget only after the child has PAUSED, and a child stuck
/// <c>Queued</c> never pauses), so the step dies on its attempt timeout and
/// leaves an orphaned child that later claims and deploys under a parent that
/// already reported failure. The residual of exempting children — a child's
/// waves interleaving with unrelated consenting work on a shared box — is
/// bounded by the F5 agent gate, which still takes the writer side per wave for
/// a non-consenting child.
/// </para>
/// <para>
/// <b>Ordering.</b> FIFO by overlap (C5): a claim defers when any in-flight task
/// conflicts OR any OLDER already-due Queued task conflicts. Only conflicting
/// pairs order the queue. Convoying is accepted and made legible via the reason
/// surface below.
/// </para>
/// <para>
/// All evaluators here build on ONE query shape
/// (<see cref="ConflictingTasksQuery"/>) so the claim's in-lock check, the
/// worker's pre-gate skip, the first-deferral log line and the task-detail
/// reason read can never drift — the same discipline F1 keeps via
/// <c>ServerTaskLease.ClaimDeferralPredicate</c>.
/// </para>
/// </summary>
public static class ServerTaskTargetExclusion
{
    /// <summary>The fixed prefix of every target-wait sentence. Purely
    /// presentational — the first-deferral dedup probe keys on
    /// <see cref="TargetWaitLogLevel"/>, so the copy can be reworded without
    /// breaking idempotence for tasks already queued.</summary>
    public const string MessagePrefix = "Waiting for target ";

    /// <summary>The log LEVEL of the one-time first-deferral banner line — the
    /// durable dedup marker <see cref="TryAppendFirstDeferralLogAsync"/> probes
    /// for. A dedicated value rather than the message text, because the step -1
    /// banner lane is shared (offline import, orchestrator banners) and the
    /// operator-visible copy must stay free to change. Unknown levels render as
    /// plain info in <c>TaskLogView</c> and count as neither error nor warning
    /// in compaction.</summary>
    public const string TargetWaitLogLevel = "target-wait";

    /// <summary>
    /// The tasks that conflict with <paramref name="taskId"/> right now: any task
    /// (either kind, excluding the task itself and its ancestor chain) that is
    /// IN-FLIGHT (<see cref="DeploymentStatusExtensions.InFlightAfterClaim"/>) or
    /// — for a TOP-LEVEL claimant only — an OLDER already-due <c>Queued</c>
    /// sibling (FIFO by overlap), and that shares at least one SERIAL target
    /// (<c>!Target.AllowParallelTaskExecution</c>) with it — unless BOTH sources
    /// consent (<paramref name="sourceConsent"/> and the other task's own source
    /// flag), in which case the pair is mutual-Shared on every shared target and
    /// never conflicts.
    /// <para>
    /// Callers MUST skip this predicate for a CHILD task (see the class remarks):
    /// children are exempt outright, which is why there is no ancestor-chain
    /// parameter here — a top-level claimant has no ancestors to exclude.
    /// </para>
    /// <para>
    /// Target sets are read live from <c>task_target_assignments</c> (they exist
    /// from creation for both kinds). Runs filter-free because the claim path has
    /// no ambient Space — NOT because conflicts cross Spaces: the assignment
    /// join's composite Space FKs pin task and target to one Space, so two tasks
    /// sharing a <c>TargetId</c> are same-Space by construction (which is also
    /// why the reason surface built on this query can never leak another Space's
    /// names).
    /// </para>
    /// </summary>
    public static IQueryable<ServerTask> ConflictingTasksQuery(
        KrakenDbContext db,
        Guid taskId,
        bool sourceConsent,
        DateTimeOffset createdUtc,
        DateTimeOffset now)
    {
        var query = db.ServerTasks
            .IgnoreQueryFilters()
            .Where(o => o.Id != taskId
                && (DeploymentStatusExtensions.InFlightAfterClaim.Contains(o.Status)
                    || (o.Status == DeploymentStatus.Queued
                        && (o.ScheduledFor == null || o.ScheduledFor <= now)
                        && o.CreatedUtc < createdUtc))
                // A shared SERIAL target: one of MY assignments whose target has
                // not opted the whole box into sharing, that also appears in the
                // other task's assignment set.
                && db.TaskTargetAssignments.IgnoreQueryFilters().Any(mine =>
                    mine.TaskId == taskId
                    && !mine.Target!.AllowParallelTaskExecution
                    && o.Targets.Any(theirs => theirs.TargetId == mine.TargetId)));

        if (sourceConsent)
        {
            // My source consents, so the pair is only a conflict when the OTHER
            // side does not: drop tasks whose own source also consents. Without
            // my consent this clause is unreachable (one Exclusive side is
            // enough), so the subqueries are skipped entirely.
            //
            // The blocker's consent is read LIVE, not as-of-its-claim (no
            // claimed-mode column exists): flipping a source flag ON while its
            // exclusive plan is mid-flight lets a consenting peer co-claim the
            // box and interleave at wave boundaries. ACCEPTED window — it takes
            // a deliberate operator flip, the F5 agent gate still serializes
            // within-wave, and flipping OFF is always safe (over-blocks only).
            query = query.Where(o => !(
                (o.Kind == ServerTaskKind.Deployment
                    && db.Projects.IgnoreQueryFilters()
                        .Any(p => p.Id == o.ProjectId && p.AllowParallelTaskExecution))
                || db.RunbookRuns.IgnoreQueryFilters()
                    .Any(rr => rr.Id == o.Id && rr.Runbook.AllowParallelTaskExecution)));
        }

        return query;
    }

    /// <summary>
    /// The claiming task's SOURCE consent: the owning project's flag for a
    /// deployment, the runbook's own flag for a run. Read live so an author's
    /// flip applies to the next claim, mirroring how the plan builder reads the
    /// target flag at dispatch time.
    /// </summary>
    public static Task<bool> SourceConsentAsync(
        KrakenDbContext db, ServerTask task, CancellationToken ct = default)
        => task is RunbookRun run
            ? db.Runbooks.IgnoreQueryFilters()
                .Where(r => r.Id == run.RunbookId)
                .Select(r => r.AllowParallelTaskExecution)
                .FirstOrDefaultAsync(ct)
            : db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == task.ProjectId)
                .Select(p => p.AllowParallelTaskExecution)
                .FirstOrDefaultAsync(ct);

    /// <summary>Row-shape overload of <see cref="SourceConsentAsync(KrakenDbContext,
    /// ServerTask, CancellationToken)"/> for callers that probed the task without
    /// materializing the entity (the worker's pre-gate probe): a run's flag is
    /// correlated through its own row instead of a loaded <c>RunbookId</c>.</summary>
    public static Task<bool> SourceConsentAsync(
        KrakenDbContext db, ServerTaskKind kind, Guid projectId, Guid taskId,
        CancellationToken ct = default)
        => kind == ServerTaskKind.RunbookRun
            ? db.RunbookRuns.IgnoreQueryFilters()
                .Where(rr => rr.Id == taskId)
                .Select(rr => rr.Runbook.AllowParallelTaskExecution)
                .FirstOrDefaultAsync(ct)
            : db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId)
                .Select(p => p.AllowParallelTaskExecution)
                .FirstOrDefaultAsync(ct);

    /// <summary>One target-wait blocker, resolved for the reason surface: the
    /// task detail banner and the first-deferral log line render
    /// <see cref="Format"/> of this.</summary>
    public sealed record TargetConflict(
        Guid BlockerTaskId,
        ServerTaskKind BlockerKind,
        string BlockerLabel,
        bool BlockerInFlight,
        Guid TargetId,
        string TargetName,
        int QueuedAhead);

    /// <summary>
    /// Resolves the target-wait reason for a still-<c>Queued</c> task: the
    /// blocking task (an in-flight conflict first; otherwise the oldest queued
    /// conflict — the one that will claim next), the shared serial target, and
    /// how many conflicting queued tasks are ahead in FIFO order. Returns
    /// <c>null</c> when nothing conflicts (the task is waiting on something
    /// else) and for a CHILD task (children are exempt from the exclusion, so
    /// they never wait on a target). Point-in-time and advisory — the
    /// authoritative gate is the advisory-locked claim; this only explains it.
    /// <para>
    /// Cost is INDEPENDENT of the conflict count: the ranking runs in SQL and the
    /// four label/target lookups are projected on the single winning row, with one
    /// extra aggregate for the queue position. Two statements total — the detail
    /// pages poll this every 5 s while a task is Queued, and the worker calls it
    /// on every deferral, so a per-conflict projection would fan out badly on a
    /// busy machine.
    /// </para>
    /// </summary>
    public static async Task<TargetConflict?> DescribeConflictAsync(
        KrakenDbContext db, Guid taskId, DateTimeOffset now, CancellationToken ct = default)
    {
        // Self-contained by id so the worker's refusal paths and the detail
        // pages can call it without a materialized entity.
        var task = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.Kind, t.ProjectId, t.CreatedUtc, t.ParentTaskId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (task is null || task.ParentTaskId is not null)
        {
            return null;
        }

        var sourceConsent = await SourceConsentAsync(db, task.Kind, task.ProjectId, taskId, ct)
            .ConfigureAwait(false);
        var conflicts = ConflictingTasksQuery(db, taskId, sourceConsent, task.CreatedUtc, now);

        // The blocker the operator should look at: an in-flight conflict (it is
        // actually occupying the target) over the oldest queued one (it merely
        // claims next); ties broken by age so the message is stable. Ordered and
        // limited in SQL, so the label subqueries run for ONE row.
        var blocker = await conflicts
            .OrderByDescending(o =>
                DeploymentStatusExtensions.InFlightAfterClaim.Contains(o.Status) ? 1 : 0)
            .ThenBy(o => o.CreatedUtc)
            .Select(o => new
            {
                o.Id,
                o.Kind,
                o.Status,
                ProjectName = db.Projects.IgnoreQueryFilters()
                    .Where(p => p.Id == o.ProjectId)
                    .Select(p => p.Name)
                    .FirstOrDefault(),
                ReleaseVersion = db.Deployments.IgnoreQueryFilters()
                    .Where(d => d.Id == o.Id)
                    .Select(d => d.Release.Version)
                    .FirstOrDefault(),
                RunbookName = db.RunbookRuns.IgnoreQueryFilters()
                    .Where(rr => rr.Id == o.Id)
                    .Select(rr => rr.Runbook.Name)
                    .FirstOrDefault(),
                // The first shared serial target, for the "Waiting for target X"
                // sentence. Any one is representative; assignment order keeps it
                // stable across reads.
                SharedTarget = db.TaskTargetAssignments.IgnoreQueryFilters()
                    .Where(mine => mine.TaskId == taskId
                        && !mine.Target!.AllowParallelTaskExecution
                        && o.Targets.Any(theirs => theirs.TargetId == mine.TargetId))
                    .OrderBy(mine => mine.AddedUtc)
                    .Select(mine => new { mine.TargetId, mine.Target!.Name })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (blocker is null)
        {
            return null;
        }

        // Queue position: conflicting tasks that will claim before this one. Only
        // Queued rows count (an in-flight blocker is reported by the verb, not the
        // position), and the predicate's FIFO arm already scopes them to OLDER.
        var queuedAhead = await conflicts
            .CountAsync(o => o.Status == DeploymentStatus.Queued, ct)
            .ConfigureAwait(false);

        var label = blocker.Kind == ServerTaskKind.RunbookRun
            ? $"Runbook {blocker.RunbookName ?? "(unnamed)"}"
            : $"Deploy {blocker.ProjectName ?? "(unknown project)"} {blocker.ReleaseVersion}".TrimEnd();

        return new TargetConflict(
            BlockerTaskId:  blocker.Id,
            BlockerKind:    blocker.Kind,
            BlockerLabel:   label,
            BlockerInFlight: DeploymentStatusExtensions.InFlightAfterClaim.Contains(blocker.Status),
            TargetId:       blocker.SharedTarget?.TargetId ?? Guid.Empty,
            TargetName:     blocker.SharedTarget?.Name ?? "(unknown)",
            QueuedAhead:    queuedAhead);
    }

    /// <summary>The single formatter for the target-wait reason — the task
    /// detail banner and the one-time task-log line must render the SAME
    /// sentence (extends F1's <c>QueueWaitMessage</c> discipline; the pages call
    /// through <c>QueueWaitMessage.TargetWait</c>).</summary>
    public static string Format(TargetConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var verb = conflict.BlockerInFlight ? "busy with" : "behind";
        // The common deferral is a single in-flight blocker with nothing queued
        // behind it, so a bare count would read "0 tasks ahead" — which operators
        // report as a bug in the queue display.
        var ahead = conflict.QueuedAhead switch
        {
            0 => "next in line",
            1 => "1 task ahead",
            _ => $"{conflict.QueuedAhead} tasks ahead",
        };
        return $"{MessagePrefix}{conflict.TargetName} — {verb} " +
               $"#{conflict.BlockerTaskId.ToString()[..8]} ({conflict.BlockerLabel}); {ahead}.";
    }

    /// <summary>
    /// Appends the target-wait reason to the task's log ON THE FIRST DEFERRAL
    /// ONLY — a blocked task is re-claimed every minute by the stale-Queued
    /// re-signal, and one line per minute would bury the log. Idempotence is a
    /// DB probe for an earlier <see cref="TargetWaitLogLevel"/> banner line
    /// (crash-safe and process-agnostic, unlike an in-memory flag; keyed to the
    /// dedicated level, never to the operator-visible copy), and probe+append run
    /// under a per-task advisory lock so racing duplicate wake-ups cannot both
    /// append. Staging is never compacted while a task is still <c>Queued</c>,
    /// so the probe only needs <c>task_log_live</c>. The append is also gated on
    /// the row still being <c>Queued</c>, inside the same transaction — a
    /// duplicate wake-up racing an operator's cancel must not stamp a permanent
    /// "waiting" line into a Cancelled task's log. Returns whether a line was
    /// written.
    /// </summary>
    public static async Task<bool> TryAppendFirstDeferralLogAsync(
        KrakenDbContext db, Guid taskId, string message, TimeProvider time,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrEmpty(message);

        var lockKey = FirstDeferralLockKey(taskId);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.Database
                .ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct)
                .ConfigureAwait(false);

            var alreadyLogged = await db.TaskLogLive
                .AnyAsync(l => l.TaskId == taskId
                            && l.StepIndex == -1
                            && l.Level == TargetWaitLogLevel, ct)
                .ConfigureAwait(false);
            if (alreadyLogged)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            // Fresh read inside the transaction: the refusal that led here is
            // status-blind about the claiming row itself (the claim checks
            // conflicts before its own Queued guard), so re-check before the
            // durable write.
            var stillQueued = await db.ServerTasks
                .IgnoreQueryFilters()
                .AnyAsync(t => t.Id == taskId && t.Status == DeploymentStatus.Queued, ct)
                .ConfigureAwait(false);
            if (!stillQueued)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            await TaskLogService.AppendLiveAsync(
                    db, taskId, stepIndex: -1, targetId: null, level: TargetWaitLogLevel,
                    message, time.GetUtcNow(), ct)
                .ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>Per-task advisory key for the first-deferral log guard — the
    /// task id folded with a fixed purpose salt so it can never collide with the
    /// claim-decision lock's constant key space by construction of use (both are
    /// transaction-scoped and held for milliseconds; a stray hash collision would
    /// only briefly serialize two unrelated appends).</summary>
    private static long FirstDeferralLockKey(Guid taskId)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime  = 1099511628211UL;

        var hash = offset;
        foreach (var b in "kraken:first-deferral"u8)
        {
            hash = unchecked((hash ^ b) * prime);
        }
        Span<byte> guid = stackalloc byte[16];
        taskId.TryWriteBytes(guid);
        foreach (var b in guid)
        {
            hash = unchecked((hash ^ b) * prime);
        }
        return unchecked((long)hash);
    }
}

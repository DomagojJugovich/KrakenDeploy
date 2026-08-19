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
/// here). A task never conflicts with its own ANCESTOR chain
/// (<c>Octopus.DeployRelease</c> children continue an already-claimed parent).
/// Ad-hoc scripts are not <c>server_tasks</c> and stay invisible to this
/// predicate (accepted wave-gap residual); they take the READ side of the agent
/// gate instead. The unit of exclusion is the <c>DeploymentTarget</c> ROW, not
/// the physical machine: one box registered as two targets (nothing enforces
/// <c>MachineName</c> uniqueness) is two identities that never conflict — the
/// same pre-existing aliasing residual the agent gate documents in
/// <c>node-concurrency-and-cache.md</c>.
/// </para>
/// <para>
/// <b>Ordering.</b> FIFO by overlap (C5): a claim defers when any in-flight task
/// conflicts OR any OLDER already-due Queued task conflicts. Only conflicting
/// pairs order the queue. Convoying is accepted and made legible via the reason
/// surface below. A CHILD task is exempt from the queued-FIFO arm entirely (it
/// defers only to IN-FLIGHT conflicts): a child is the continuation of a parent
/// that already holds the target, so an older queued unrelated task can never
/// go first anyway — making the child wait for it is a three-way deadlock
/// (child → queued task → in-flight parent → child) that burns the parent's
/// whole child-wait budget. Same reasoning as the maintenance gate's child
/// exemption in <c>ServerTaskLease.TryClaimAsync</c>.
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
    /// <paramref name="isChild"/> disables the queued-FIFO arm: a child claims
    /// while its parent already HOLDS the shared target, so every older queued
    /// conflicting task is itself deferring to that parent — waiting for it is a
    /// circular wait (child → queued task → in-flight parent → child), not
    /// fairness. In-flight conflicts (an unrelated task genuinely occupying the
    /// target) still defer the child; those resolve independently.
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
        IReadOnlyCollection<Guid> ancestorIds,
        bool isChild,
        DateTimeOffset createdUtc,
        DateTimeOffset now)
    {
        var query = db.ServerTasks
            .IgnoreQueryFilters()
            .Where(o => o.Id != taskId
                && !ancestorIds.Contains(o.Id)
                && (DeploymentStatusExtensions.InFlightAfterClaim.Contains(o.Status)
                    || (!isChild
                        && o.Status == DeploymentStatus.Queued
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

    /// <summary>
    /// The task's ancestor chain (parent, grandparent, …), walked via
    /// <c>ParentTaskId</c>. Immutable after creation, so callers may read it
    /// outside the claim transaction. Empty for a top-level task — the common
    /// case costs zero queries. A cycle (impossible by construction — a parent is
    /// claimed before its child exists) terminates the walk rather than hanging it.
    /// </summary>
    public static Task<IReadOnlyList<Guid>> LoadAncestorChainAsync(
        KrakenDbContext db, ServerTask task, CancellationToken ct = default)
        => LoadAncestorChainByParentAsync(db, task.ParentTaskId, ct);

    /// <summary>Core of <see cref="LoadAncestorChainAsync"/> for callers holding
    /// only the probed <c>ParentTaskId</c>.</summary>
    public static async Task<IReadOnlyList<Guid>> LoadAncestorChainByParentAsync(
        KrakenDbContext db, Guid? parentTaskId, CancellationToken ct = default)
    {
        if (parentTaskId is null)
        {
            return [];
        }

        var chain = new List<Guid>();
        var current = parentTaskId;
        while (current is { } id && !chain.Contains(id))
        {
            chain.Add(id);
            current = await db.ServerTasks
                .IgnoreQueryFilters()
                .Where(t => t.Id == id)
                .Select(t => t.ParentTaskId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }
        return chain;
    }

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
    /// else). Point-in-time and advisory — the authoritative gate is the
    /// advisory-locked claim; this only explains it.
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
        if (task is null)
        {
            return null;
        }

        var ancestors = await LoadAncestorChainByParentAsync(db, task.ParentTaskId, ct)
            .ConfigureAwait(false);
        var sourceConsent = await SourceConsentAsync(db, task.Kind, task.ProjectId, taskId, ct)
            .ConfigureAwait(false);

        var conflicts = await ConflictingTasksQuery(
                db, taskId, sourceConsent, ancestors,
                isChild: task.ParentTaskId is not null, task.CreatedUtc, now)
            .Select(o => new
            {
                o.Id,
                o.Kind,
                o.Status,
                o.CreatedUtc,
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
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (conflicts.Count == 0)
        {
            return null;
        }

        // The blocker the operator should look at: an in-flight conflict (it is
        // actually occupying the target) over the oldest queued one (it merely
        // claims next); ties broken by age so the message is stable.
        var blocker = conflicts
            .OrderByDescending(c => DeploymentStatusExtensions.InFlightAfterClaim.Contains(c.Status))
            .ThenBy(c => c.CreatedUtc)
            .First();
        var queuedAhead = conflicts.Count(c => c.Status == DeploymentStatus.Queued);

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
        var ahead = conflict.QueuedAhead == 1 ? "1 task ahead" : $"{conflict.QueuedAhead} tasks ahead";
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

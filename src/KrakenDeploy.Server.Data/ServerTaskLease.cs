using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Maintenance;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// B1 durable dispatch — the atomic <c>Queued→Running</c> claim and its lease.
/// <para>
/// The DB row is the source of truth for dispatch; the in-process channels are
/// only wake-up signals. Wake-ups are therefore AT-LEAST-ONCE (create-time
/// enqueue, the minutely dispatch job, the startup/sweep reconciler can all
/// signal the same task) and this claim is what makes execution EXACTLY-ONCE:
/// one conditional <c>UPDATE … WHERE status = Queued</c> wins, every other
/// wake-up loses and bails. The same condition means a task cancelled while
/// queued can never be claimed — the previous read-then-blind-write TOCTOU is
/// closed.
/// </para>
/// <para>
/// The claim stamps a LEASE (<see cref="ServerTask.LeaseUntil"/>): the owning
/// worker renews it while the dispatch is in flight, and the reconciler treats
/// an expired lease on a <c>Running</c> deployment as "the owning process is
/// dead". Ownership-by-lease (not by instance name) is what keeps a blue-green
/// overlap safe: the draining slot keeps renewing, so the freshly booted slot
/// never touches its live runs.
/// </para>
/// </summary>
public static class ServerTaskLease
{
    /// <summary>How long a claim stays valid without renewal. Five missed
    /// renewals in a row (worker crash, hard hang) before recovery kicks in.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How often the owning worker renews an in-flight lease.</summary>
    public static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(1);

    /// <summary>Forensic claim-owner label for this process. Liveness decisions
    /// use the lease expiry, never this value — two blue-green slots on one
    /// machine share a name shape and that must not matter.</summary>
    public static readonly string ProcessOwner =
        string.Create(CultureInfo.InvariantCulture,
            $"kraken:{Environment.MachineName}:pid{Environment.ProcessId}");

    /// <summary>
    /// Convenience overload for callers that hold only the id (tests, ad-hoc
    /// paths): loads the task's serialization key and delegates. The production
    /// dispatch paths already have the <see cref="ServerTask"/> loaded and pass it
    /// to the entity overload, which skips this extra read.
    /// </summary>
    public static async Task<ServerTaskClaimResult> TryClaimAsync(
        KrakenDbContext db, Guid taskId, TimeProvider time, CancellationToken ct = default)
    {
        var task = await db.ServerTasks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct)
            .ConfigureAwait(false);
        return task is null
            ? ServerTaskClaimResult.NotQueued // row gone (deleted between wake-up and claim)
            : await TryClaimAsync(db, task, time, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claims the task for execution: <c>Queued→Running</c>, stamps
    /// <c>StartedUtc</c> + the lease, and clears <c>ScheduledFor</c> (once
    /// claimed, the scheduled-dispatch job must never see the row again). Reads
    /// the serialization key straight off <paramref name="task"/> (immutable after
    /// creation) — no extra DB round-trip.
    /// <para>
    /// <b>F1 — (project, environment, tenant) serialization.</b> A
    /// <c>Deployment</c> is claimed only when NO other deployment of the same
    /// <c>(ProjectId, EnvironmentId, TenantId)</c> should go first — that is, none
    /// is IN-FLIGHT (<see cref="DeploymentStatusExtensions.InFlightAfterClaim"/>:
    /// <c>Running</c> or a parked <c>PendingOfflineResult</c>) AND no earlier,
    /// already-due <c>Queued</c> sibling is still waiting (FIFO — the oldest queued
    /// deployment of a key goes next, Octopus-parity; a future-scheduled sibling is
    /// NOT yet due and never blocks). NULL tenant is its own key.
    /// <c>RunbookRun</c> is EXEMPT from this rule (operational tooling; runbooks
    /// of one project may run concurrently).
    /// </para>
    /// <para>
    /// <b>F6 — per-plan target exclusion.</b> BOTH kinds additionally defer when
    /// they share a SERIAL target with any in-flight task or any older
    /// already-due queued one (FIFO by overlap; mutual-Shared overlap neither
    /// defers nor orders; a CHILD task is exempt outright) — see
    /// <see cref="ServerTaskTargetExclusion"/>. All checks + the claim run inside
    /// ONE transaction under ONE GLOBAL advisory lock
    /// (<see cref="ClaimDecisionLockKey"/> — it subsumed F1's per-key lock) so
    /// two concurrent claimants cannot both proceed: the lock-loser blocks until
    /// the winner commits, then its <b>fresh-per-statement</b> read
    /// (READ COMMITTED) sees the winner's committed row and is refused.
    /// </para>
    /// <para>
    /// Returns <see cref="ServerTaskClaimResult.NotQueued"/> when the row was not
    /// <c>Queued</c> anymore (already claimed by another wake-up, cancelled, or
    /// gone), <see cref="ServerTaskClaimResult.SerializationBlocked"/> when a
    /// same-key deployment is in-flight or an earlier sibling is ahead, and
    /// <see cref="ServerTaskClaimResult.TargetBlocked"/> when a serial target is
    /// held (F6); in all three cases the caller must bail without dispatching.
    /// The task stays <c>Queued</c> and the minutely stale-Queued re-signal
    /// (<see cref="Jobs.ScheduledDeploymentDispatchJob"/> — kind-agnostic, so it
    /// covers runbook runs too) retries it.
    /// </para>
    /// <para>
    /// <b>Maintenance gate.</b> Returns
    /// <see cref="ServerTaskClaimResult.MaintenanceBlocked"/> while instance-wide
    /// maintenance mode is on, so enabling maintenance actually STOPS THE QUEUE
    /// rather than only walling off HTTP mutations. The gate lives here because the
    /// claim is the single choke point for <c>Queued→Running</c>: every wake-up
    /// source (create-time enqueue, the minutely re-signal, the boot reconciler)
    /// funnels through it, so one check covers them all — gating the dispatch job
    /// instead would leave the create-time path wide open, and would wrongly also
    /// stop that job's orphan-reconciliation arm, which MUST keep running through a
    /// restart-heavy maintenance window.
    /// </para>
    /// <para>
    /// A CHILD task (<c>ParentTaskId != null</c>, spawned by an
    /// <c>Octopus.DeployRelease</c> step) is EXEMPT: it is the continuation of an
    /// already-claimed parent, not new work, and blocking it would strand the
    /// parent's <c>WaitForChildAsync</c> behind a child that can never claim —
    /// while the parent keeps renewing its lease, so the reconciler never reaps it
    /// either. Same reasoning as the E3 <c>NodeTaskGate</c> bypass in
    /// <c>DeploymentWorker.GateThenDispatchCoreAsync</c>.
    /// </para>
    /// </summary>
    public static async Task<ServerTaskClaimResult> TryClaimAsync(
        KrakenDbContext db, ServerTask task, TimeProvider time, CancellationToken ct = default)
    {
        // Maintenance gate, ahead of the kind branch so it covers deployments AND
        // runbook runs. Read straight off the claim's own context — cache-free (the
        // SettingsService instance cache has a 10 s TTL that would let a burst of
        // tasks through after the operator flips the switch) and, on the Deployment
        // path below, on the same connection as the claim itself. The residual
        // window is one in-flight claim racing the enable commit, which no gate can
        // close and which the operator already tolerates for tasks claimed a
        // millisecond earlier.
        if (task.ParentTaskId is null)
        {
            var maintenance = await SettingsService
                .ReadOrDefaultAsync<MaintenanceSettings>(db, ct: ct)
                .ConfigureAwait(false);
            if (maintenance.Enabled)
            {
                return ServerTaskClaimResult.MaintenanceBlocked;
            }
        }

        // Both kinds now claim inside ONE user-initiated transaction under ONE
        // GLOBAL advisory lock (F6). The web host's NpgsqlRetryingExecutionStrategy
        // only permits a user transaction when driven THROUGH the execution
        // strategy (a bare BeginTransactionAsync throws there). The strategy
        // re-runs the whole delegate on a transient fault; the body is safe to
        // repeat — the worst case is a false NotQueued after a
        // commit-then-transient-fault, which only makes the worker bail on a row
        // it truly claimed (the reconciler then fails that ownerless Running row).
        // It can never double-claim, so both serialization invariants hold.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Blocking, transaction-scoped advisory lock — auto-released at
            // commit/rollback. ONE constant key for EVERY claim decision (F6,
            // locked decision P1): it REPLACES F1's per-(project,env,tenant) key,
            // subsuming it — the target-conflict predicate compares set-valued
            // target overlaps, which per-key locks cannot serialize (two claimants
            // with different F1 keys can still share a target). Claims hold the
            // lock for single-digit milliseconds, so the global choke point is
            // not a throughput hazard; correctness still comes from the
            // fresh-per-statement READ COMMITTED reads below, never from the
            // lock's key shape. The ACCEPTED tradeoff of the constant key: one
            // wedged claim transaction (a half-open connection holding the tx,
            // a statement riding out its command timeout across the strategy's
            // retries) stalls EVERY claim instance-wide, where the per-key lock
            // confined the stall to one key — the B1 lease/reconciler and the
            // minutely re-signal recover the queue once the holder dies.
            // FormattableString → bound parameter.
            await db.Database
                .ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ClaimDecisionLockKey})", ct)
                .ConfigureAwait(false);

            // The claim's timestamp is taken AFTER the lock, inside the retry
            // delegate: it stamps LeaseUntil = now + 5 min, and a value captured
            // before a long lock wait (or carried across a strategy retry) could
            // commit a lease that is already expired — reconciler arm 4 would
            // then fail a genuinely-owned run before its first renewal.
            var now = time.GetUtcNow();

            // F1 — deployments only (runbook runs stay exempt from the
            // (project,env,tenant) rule). Separate statement (fresh READ COMMITTED
            // snapshot AFTER the lock): the lock-loser sees the winner's
            // just-committed row here. Defer if a same-key peer is in-flight OR an
            // earlier-queued due sibling waits.
            if (task.Kind == ServerTaskKind.Deployment)
            {
                var deferred = await db.ServerTasks
                    .IgnoreQueryFilters()
                    .AnyAsync(
                        ClaimDeferralPredicate(
                            task.Id, task.ProjectId, task.EnvironmentId, task.TenantId,
                            task.CreatedUtc, now),
                        ct)
                    .ConfigureAwait(false);
                if (deferred)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return ServerTaskClaimResult.SerializationBlocked;
                }
            }

            // F6 — per-plan target exclusion, BOTH kinds (fully symmetric): defer
            // when any in-flight task, or any older already-due Queued task,
            // shares a serial target with this one (see ServerTaskTargetExclusion).
            // Checked after F1 so a same-key sibling reports the more specific
            // SerializationBlocked.
            //
            // A CHILD task is EXEMPT outright — its targets are already held by
            // the parent that spawned it, so the parent's claim accounted for
            // them, and deferring a child to anything strands the parent that is
            // blocking on it (the same exemption the maintenance gate above and
            // the E3 NodeTaskGate make, for the same reason). See the class
            // remarks on ServerTaskTargetExclusion for why a partial exemption is
            // not enough.
            if (task.ParentTaskId is null)
            {
                var sourceConsent = await ServerTaskTargetExclusion
                    .SourceConsentAsync(db, task, ct)
                    .ConfigureAwait(false);
                var targetConflict = await ServerTaskTargetExclusion
                    .ConflictingTasksQuery(db, task.Id, sourceConsent, task.CreatedUtc, now)
                    .AnyAsync(ct)
                    .ConfigureAwait(false);
                if (targetConflict)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return ServerTaskClaimResult.TargetBlocked;
                }
            }

            var result = await ClaimConditionalAsync(db, task.Id, now, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The conditional <c>Queued→Running</c> claim itself (kind-agnostic): the
    /// <c>Status = Queued</c> guard is what closes the cancel-vs-claim and
    /// duplicate-wake-up TOCTOU. Returns <see cref="ServerTaskClaimResult.Claimed"/>
    /// on the single winning row, else <see cref="ServerTaskClaimResult.NotQueued"/>.
    /// </summary>
    private static async Task<ServerTaskClaimResult> ClaimConditionalAsync(
        KrakenDbContext db, Guid taskId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId && t.Status == DeploymentStatus.Queued)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Running)
                    .SetProperty(t => t.StartedUtc, now)
                    .SetProperty(t => t.ClaimedBy, ProcessOwner)
                    .SetProperty(t => t.LeaseUntil, now + LeaseDuration)
                    .SetProperty(t => t.ScheduledFor, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return rows == 1 ? ServerTaskClaimResult.Claimed : ServerTaskClaimResult.NotQueued;
    }

    /// <summary>
    /// WP3 — resumes a task parked at a manual-intervention gate:
    /// <c>Paused → Running</c> with a fresh lease. One conditional
    /// <c>UPDATE … WHERE status = Paused</c>, so a duplicate wake-up (approve +
    /// reconciler arm 3 both signalling) resolves to exactly one resumer, and a task
    /// cancelled while paused can never be resumed — the same TOCTOU closure
    /// <see cref="TryClaimAsync"/> gets from its <c>Queued</c> guard.
    /// <para>
    /// <b>No F1 (or F6) re-check, deliberately.</b> A <c>Paused</c> task is in
    /// <see cref="DeploymentStatusExtensions.InFlightAfterClaim"/>, so it never
    /// released its <c>(project, environment, tenant)</c> key — nor its TARGETS:
    /// other tasks' target-conflict checks still see it as in-flight and defer to
    /// it. Re-running either deferral predicate would only let a task lose a
    /// slot it already owns — to a peer that, by construction, cannot exist.
    /// </para>
    /// <para>
    /// <c>StartedUtc</c> is NOT restamped: the run started before it paused, and the
    /// slow-task audits + the AI-diagnosis gate read it as the true start.
    /// </para>
    /// <para>
    /// The maintenance gate DOES apply, with the SAME parent-task exemption
    /// <see cref="TryClaimAsync"/> makes: while maintenance mode is on nothing new may
    /// reach an agent, and a paused root task whose gate is already answered simply stays
    /// <c>Paused</c> — reconciler arm 3 (the resolved-gate arm; arm 4 is the orphan
    /// reaper and only sees <c>Running</c> rows) re-signals it once the operator disables
    /// maintenance, exactly as the stale-<c>Queued</c> arm does for a blocked claim. A
    /// CHILD task is exempt, because blocking it strands the parent that is waiting on
    /// it. Returns
    /// <see cref="ServerTaskClaimResult.NotQueued"/> when the row was no longer
    /// <c>Paused</c> (cancelled, or resumed by another wake-up), OR its gate is still
    /// <c>Pending</c> — a duplicate wake-up must not resume an unanswered gate.
    /// <see cref="ServerTaskClaimResult.SerializationBlocked"/> is never returned.
    /// </para>
    /// </summary>
    public static async Task<ServerTaskClaimResult> TryResumeAsync(
        KrakenDbContext db, Guid taskId, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);

        // Cheap pre-read of just the two fields the maintenance gate needs, and it
        // doubles as the "is this row even resumable" check — so the ordinary case (a
        // minutely re-signal of a row that has already resumed) costs one small SELECT
        // instead of a settings read it cannot act on.
        var meta = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.Status, t.ParentTaskId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (meta is null || meta.Status != DeploymentStatus.Paused)
        {
            return ServerTaskClaimResult.NotQueued;
        }

        // Maintenance mode blocks a resume, but NOT a child task — the same exemption
        // TryClaimAsync makes, for the same reason (WP3-b restored it here). Blocking a
        // child strands the parent's WaitForChildAsync behind a child that can never
        // resume, while the parent keeps renewing its lease so the reconciler never reaps
        // it either: the parent burns its whole child-wait ceiling holding the F1 key
        // while a human's approval sits recorded and unusable.
        if (meta.ParentTaskId is null)
        {
            var maintenance = await SettingsService
                .ReadOrDefaultAsync<MaintenanceSettings>(db, ct: ct)
                .ConfigureAwait(false);
            if (maintenance.Enabled)
            {
                return ServerTaskClaimResult.MaintenanceBlocked;
            }
        }

        var now = time.GetUtcNow();
        var rows = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId && t.Status == DeploymentStatus.Paused
                     // The gate must be ANSWERED, not merely present. Status alone is
                     // not enough: wake-ups are at-least-once, so a gated task that
                     // waited past the stale-Queued grace accumulates duplicate channel
                     // items. Dispatch #1 claims, pauses and frees its slot; parked
                     // dispatch #2 then found the row Paused, resumed it, and the
                     // orchestrator hard-failed the task for the "impossible" state of
                     // Running with a Pending gate — killing the deployment seconds
                     // after it paused, before any human could answer. The old
                     // Queued-only claim ate duplicates for free; this restores that
                     // property for the pause path.
                     // The inner IgnoreQueryFilters is redundant with the root one (the
                     // flag is per-statement in EF Core 10, subqueries included); kept
                     // so this guard stays correct if it is ever split out on its own.
                     && !db.Interruptions
                         .IgnoreQueryFilters()
                         .Any(i => i.TaskId == taskId
                                && i.Status == InterruptionStatus.Pending))
            .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, DeploymentStatus.Running)
                    .SetProperty(t => t.ClaimedBy, ProcessOwner)
                    .SetProperty(t => t.LeaseUntil, now + LeaseDuration)
                    // The checkpoint is consumed by this resume: clearing it in the SAME
                    // statement keeps "non-null iff Paused" a true invariant with no
                    // window, and — crucially — avoids the caller clearing it on the
                    // TRACKED entity, which would leave that entity dirty with a stale
                    // xmin and make the next SaveChanges throw (the B5 trap
                    // ServerTaskStatusWriter exists to avoid). The caller reads the
                    // payload off the entity it loaded BEFORE this call, so nothing is
                    // lost.
                    .SetProperty(t => t.PauseCheckpointEncrypted, (string?)null),
                ct)
            .ConfigureAwait(false);
        return rows == 1 ? ServerTaskClaimResult.Claimed : ServerTaskClaimResult.NotQueued;
    }

    /// <summary>
    /// Mirrors a successful <see cref="TryResumeAsync"/> onto the TRACKED entity,
    /// with the same not-modified reset <see cref="MirrorClaim"/> applies and for the
    /// same reason: the resume is an <c>ExecuteUpdate</c>, so without the mirror the
    /// tracked entity still reads <c>Paused</c>, and without the reset a later
    /// <c>SaveChanges</c> would re-assert <c>Running</c> over a <c>Cancelled</c> that
    /// landed in between.
    /// </summary>
    public static void MirrorResume(KrakenDbContext db, ServerTask task, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(time);

        var now = time.GetUtcNow();
        var entry = db.Entry(task);

        Mirror(entry.Property(t => t.Status), DeploymentStatus.Running);
        Mirror(entry.Property(t => t.ClaimedBy), ProcessOwner);
        Mirror(entry.Property(t => t.LeaseUntil), now + LeaseDuration);
        // Mirror the cleared checkpoint too — otherwise the tracked entity still holds
        // the old ciphertext and a later SaveChanges would write it BACK onto a Running
        // row, resurrecting a checkpoint the resume already consumed.
        Mirror(entry.Property(t => t.PauseCheckpointEncrypted), null);

        static void Mirror<T>(
            Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<ServerTask, T> property,
            T value)
        {
            property.CurrentValue  = value;
            property.OriginalValue = value;
            property.IsModified    = false;
        }
    }

    /// <summary>
    /// The F1 serialization predicate: another <c>Deployment</c> (kind-scoped —
    /// a runbook run never blocks a deployment) of the same
    /// <c>(ProjectId, EnvironmentId, TenantId)</c> that is currently IN-FLIGHT —
    /// claimed but not yet terminal
    /// (<see cref="DeploymentStatusExtensions.InFlightAfterClaim"/>: <c>Running</c>,
    /// a parked <c>PendingOfflineResult</c>, or a <c>Paused</c> approval gate) —
    /// excluding <paramref name="excludingTaskId"/> (a task never blocks itself). A
    /// parked offline-drop deployment (<c>PendingOfflineResult</c>) or one paused at a
    /// manual-intervention gate (<c>Paused</c>, WP3) still holds the key: it is a
    /// non-terminal deployment of that (project,env,tenant), so a new one must wait
    /// until it resolves or is cancelled (Octopus parity — "starts only after the
    /// first goes terminal"). <c>t.TenantId == tenantId</c> uses EF's null-safe
    /// comparison, so a NULL tenant matches only other NULL-tenant rows —
    /// untenanted deployments serialize among themselves. Shared by the claim's
    /// in-lock check, the worker's pre-gate skip and the UI queue-reason read so
    /// they can never drift.
    /// </summary>
    public static Expression<Func<ServerTask, bool>> InFlightDeploymentPeerPredicate(
        Guid excludingTaskId, Guid projectId, Guid environmentId, Guid? tenantId)
        => t => t.Id != excludingTaskId
             && t.Kind == ServerTaskKind.Deployment
             && DeploymentStatusExtensions.InFlightAfterClaim.Contains(t.Status)
             && t.ProjectId == projectId
             && t.EnvironmentId == environmentId
             && t.TenantId == tenantId;

    /// <summary>
    /// The claim-time deferral predicate for a <c>Deployment</c> of the given key:
    /// another same-key <c>Deployment</c> that should go FIRST — either it is
    /// IN-FLIGHT (<see cref="InFlightDeploymentPeerPredicate"/> — a Running or
    /// parked-offline peer) OR it is an earlier, <b>already-due</b> <c>Queued</c>
    /// sibling (FIFO fairness: the oldest queued deployment of a key claims next,
    /// Octopus-parity). "Earlier" is by <c>CreatedUtc</c>; a sibling whose
    /// <c>ScheduledFor</c> is still in the future is NOT due and never blocks — so
    /// a future-scheduled older deployment can't starve a ready one. Excludes
    /// <paramref name="excludingTaskId"/> (a task never defers to itself). This is
    /// the claim/pre-gate gate; the UI queue-reason uses the narrower in-flight
    /// predicate (its message is specifically "another deployment is running").
    /// A <c>CreatedUtc</c> tie falls back to the advisory-lock race (harmless —
    /// exactly one still wins). Shares
    /// <see cref="DeploymentStatusExtensions.InFlightAfterClaim"/> with the
    /// in-flight predicate so the two can't drift on which statuses count.
    /// </summary>
    public static Expression<Func<ServerTask, bool>> ClaimDeferralPredicate(
        Guid excludingTaskId, Guid projectId, Guid environmentId, Guid? tenantId,
        DateTimeOffset createdUtc, DateTimeOffset now)
        => o => o.Id != excludingTaskId
             && o.Kind == ServerTaskKind.Deployment
             && o.ProjectId == projectId
             && o.EnvironmentId == environmentId
             && o.TenantId == tenantId
             && (DeploymentStatusExtensions.InFlightAfterClaim.Contains(o.Status)
                 || (o.Status == DeploymentStatus.Queued
                     && (o.ScheduledFor == null || o.ScheduledFor <= now)
                     && o.CreatedUtc < createdUtc));

    /// <summary>
    /// The ONE constant advisory-lock key every claim decision serializes on
    /// (F6, locked decision P1) — a fixed literal (the ASCII bytes of
    /// <c>"krakenCL"</c>, CL = claim lock), stable across processes and releases
    /// by construction. It REPLACED F1's per-(project, env, tenant) key: the
    /// target-conflict predicate compares set-valued target overlaps, which
    /// per-key locks cannot serialize. Claims hold it for milliseconds; the
    /// exact-equality predicates remain the correctness gates, never the key.
    /// </summary>
    internal const long ClaimDecisionLockKey = unchecked((long)0x6B72616B656E434CUL);

    /// <summary>
    /// Extends the lease of a still-<c>Running</c> task. Returns <c>false</c>
    /// when nothing was renewed — the task reached a terminal state, or the
    /// reconciler already failed it as orphaned (a hard multi-minute stall).
    /// The caller only logs; the terminal-state guards on every final write are
    /// what protect against overwriting the reconciler's verdict.
    /// </summary>
    public static async Task<bool> TryRenewAsync(
        KrakenDbContext db, Guid taskId, TimeProvider time, CancellationToken ct = default)
    {
        var rows = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId && t.Status == DeploymentStatus.Running)
            .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.LeaseUntil, time.GetUtcNow() + LeaseDuration),
                ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>
    /// Mirrors a successful claim onto the TRACKED entity — and marks the
    /// mirrored properties NOT-modified. <see cref="TryClaimAsync"/> bypasses
    /// the change tracker (ExecuteUpdate), so without the mirror downstream
    /// logic would read stale values (e.g. the AI-diagnosis gate on
    /// <c>StartedUtc</c>); and without the not-modified reset any later
    /// <c>SaveChanges</c> on the same context would blindly re-assert
    /// <c>Running</c> over a <c>Cancelled</c> that landed in between — the exact
    /// clobber the atomic claim exists to prevent.
    /// </summary>
    public static void MirrorClaim(KrakenDbContext db, ServerTask task, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var entry = db.Entry(task);

        // For each mirrored property: set the CURRENT value, align the ORIGINAL
        // value to it, then clear the modified flag. The order matters — EF
        // resets a property's current value back to its original when IsModified
        // is set to false, so the original must already hold the new value.
        Mirror(entry.Property(t => t.Status), DeploymentStatus.Running);
        Mirror(entry.Property(t => t.StartedUtc), now);
        Mirror(entry.Property(t => t.ClaimedBy), ProcessOwner);
        Mirror(entry.Property(t => t.LeaseUntil), now + LeaseDuration);
        Mirror(entry.Property(t => t.ScheduledFor), null);

        static void Mirror<T>(
            Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<ServerTask, T> property,
            T value)
        {
            property.CurrentValue  = value;
            property.OriginalValue = value;
            property.IsModified    = false;
        }
    }

    // D1 Phase 3: ReleaseAsync (the mid-flight lease release) is gone with the
    // runbook hand-off model — every orchestration now holds its lease until a
    // terminal (or PendingOfflineResult) write clears it inline on the tracked
    // entity through the guarded status writer.
}

/// <summary>
/// Outcome of <see cref="ServerTaskLease.TryClaimAsync"/>. Only
/// <see cref="Claimed"/> lets the caller dispatch; the others all leave the
/// task <c>Queued</c> for the minutely re-signal to retry, and differ only so the
/// worker can log the reason accurately.
/// </summary>
public enum ServerTaskClaimResult
{
    /// <summary>Won the atomic <c>Queued→Running</c> claim — dispatch.</summary>
    Claimed,

    /// <summary>The row was no longer <c>Queued</c> (already claimed by another
    /// wake-up, cancelled, or gone) — bail without dispatching.</summary>
    NotQueued,

    /// <summary>F1 — another deployment of the same
    /// <c>(project, environment, tenant)</c> is <c>Running</c>; the claim was
    /// refused to keep them serialized. Bail; the task stays <c>Queued</c>.</summary>
    SerializationBlocked,

    /// <summary>F6 — the task shares a SERIAL target with an in-flight task or
    /// an older already-due queued one (see
    /// <c>ServerTaskTargetExclusion.ConflictingTasksQuery</c>); the claim was
    /// refused so no two tasks operate on that machine concurrently for the
    /// whole plan duration. Bail; the task stays <c>Queued</c> and the minutely
    /// re-signal retries it. The worker writes the one-time first-deferral
    /// task-log line on this result.</summary>
    TargetBlocked,

    /// <summary>Instance-wide maintenance mode is on, so no NEW task may start.
    /// Bail; the task stays <c>Queued</c> and claims normally the moment the
    /// operator disables maintenance (the minutely re-signal retries it — closing
    /// the window needs no extra poller). Child tasks never see this.</summary>
    MaintenanceBlocked,
}

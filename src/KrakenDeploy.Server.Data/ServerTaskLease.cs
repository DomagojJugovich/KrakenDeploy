using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using KrakenDeploy.Server.Core.Domain.Deployments;
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
    /// Atomically claims the task for execution: <c>Queued→Running</c>, stamps
    /// <c>StartedUtc</c> + the lease, and clears <c>ScheduledFor</c> (once
    /// claimed, the scheduled-dispatch job must never see the row again).
    /// <para>
    /// <b>F1 — (project, environment, tenant) serialization.</b> A
    /// <c>Deployment</c> additionally requires that no OTHER deployment of the
    /// same <c>(ProjectId, EnvironmentId, TenantId)</c> is currently
    /// <c>Running</c> (Octopus-parity "one deployment per project/env/tenant";
    /// NULL tenant is its own key — untenanted deployments serialize among
    /// themselves, different tenants proceed in parallel). The check + claim run
    /// inside <c>pg_advisory_xact_lock(hash64(project, env, tenant))</c> so two
    /// concurrent claimants of the same key cannot both see "no peer Running" and
    /// both win: the lock-loser blocks until the winner commits, then its
    /// <b>fresh-per-statement</b> peer read (READ COMMITTED) sees the winner's
    /// committed <c>Running</c> row and is refused. <c>RunbookRun</c> is EXEMPT
    /// (operational tooling; runbooks may run concurrently) — it takes the plain
    /// conditional claim, a single autonomous statement.
    /// </para>
    /// <para>
    /// Returns <see cref="ServerTaskClaimResult.NotQueued"/> when the row was not
    /// <c>Queued</c> anymore (already claimed by another wake-up, cancelled, or
    /// gone) and <see cref="ServerTaskClaimResult.SerializationBlocked"/> when a
    /// same-key deployment is running; in both cases the caller must bail without
    /// dispatching. The task stays <c>Queued</c> and the minutely stale-Queued
    /// re-signal (<see cref="Jobs.ScheduledDeploymentDispatchJob"/>) retries it.
    /// </para>
    /// </summary>
    public static async Task<ServerTaskClaimResult> TryClaimAsync(
        KrakenDbContext db, Guid taskId, TimeProvider time, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();

        // The serialization key + kind — immutable after creation, so read it
        // filter-free up front (the worker scope may have no active Space).
        var meta = await db.ServerTasks
            .IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.Kind, t.ProjectId, t.EnvironmentId, t.TenantId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (meta is null)
        {
            return ServerTaskClaimResult.NotQueued; // row gone (deleted between wake-up and claim)
        }

        // RunbookRun is exempt from serialization — plain conditional claim, one
        // autonomous statement (retry-safe as-is; no user transaction needed).
        if (meta.Kind != ServerTaskKind.Deployment)
        {
            return await ClaimConditionalAsync(db, taskId, now, ct).ConfigureAwait(false);
        }

        // Deployment: serialize on (project, env, tenant). The advisory lock +
        // the peer check + the conditional claim MUST share one transaction, so
        // it is a user-initiated transaction — which the web host's
        // NpgsqlRetryingExecutionStrategy only permits when driven THROUGH the
        // execution strategy (a bare BeginTransactionAsync throws there). The
        // strategy re-runs the whole delegate on a transient fault; the body is
        // safe to repeat — the worst case is a false NotQueued after a
        // commit-then-transient-fault, which only makes the worker bail on a row
        // it truly claimed (the reconciler then fails that ownerless Running row).
        // It can never double-claim, so the serialization invariant holds.
        var lockKey = SerializationLockKey(meta.ProjectId, meta.EnvironmentId, meta.TenantId);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Blocking, transaction-scoped advisory lock — auto-released at
            // commit/rollback. Concurrent same-key claimants serialize here;
            // different keys never contend. FormattableString → bound parameter.
            await db.Database
                .ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct)
                .ConfigureAwait(false);

            // Separate statement (fresh READ COMMITTED snapshot AFTER the lock):
            // the lock-loser sees the winner's just-committed Running row here.
            var blocked = await db.ServerTasks
                .IgnoreQueryFilters()
                .AnyAsync(
                    InFlightDeploymentPeerPredicate(
                        taskId, meta.ProjectId, meta.EnvironmentId, meta.TenantId),
                    ct)
                .ConfigureAwait(false);
            if (blocked)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return ServerTaskClaimResult.SerializationBlocked;
            }

            var result = await ClaimConditionalAsync(db, taskId, now, ct).ConfigureAwait(false);
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
    /// The F1 serialization predicate: another <c>Deployment</c> (kind-scoped —
    /// a runbook run never blocks a deployment) of the same
    /// <c>(ProjectId, EnvironmentId, TenantId)</c> that is currently IN-FLIGHT —
    /// claimed but not yet terminal
    /// (<see cref="DeploymentStatusExtensions.InFlightAfterClaim"/>: <c>Running</c>
    /// or <c>PendingOfflineResult</c>) — excluding <paramref name="excludingTaskId"/>
    /// (a task never blocks itself). A parked offline-drop deployment
    /// (<c>PendingOfflineResult</c>) still holds the key: it is a non-terminal
    /// deployment of that (project,env,tenant), so a new one must wait until it
    /// resolves or is cancelled (Octopus parity — "starts only after the first
    /// goes terminal"). <c>t.TenantId == tenantId</c> uses EF's null-safe
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
    /// Deterministic 64-bit advisory-lock key for a <c>(project, env, tenant)</c>
    /// serialization group (FNV-1a over the three GUIDs, with a discriminator byte
    /// so a NULL tenant hashes distinctly from any real tenant — N12). Must be
    /// stable across processes, so it does NOT use <see cref="object.GetHashCode"/>
    /// (randomized per run). A hash collision between two unrelated keys is
    /// harmless: it only makes their two claims briefly serialize on the same
    /// advisory lock — the exact-equality peer predicate is the correctness gate,
    /// never the hash.
    /// </summary>
    internal static long SerializationLockKey(Guid projectId, Guid environmentId, Guid? tenantId)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime  = 1099511628211UL;

        var hash = offset;
        Span<byte> guid = stackalloc byte[16];

        projectId.TryWriteBytes(guid);
        hash = Fold(hash, guid);
        environmentId.TryWriteBytes(guid);
        hash = Fold(hash, guid);

        if (tenantId is { } tenant)
        {
            hash = FoldByte(hash, 0x01); // "tenanted" discriminator
            tenant.TryWriteBytes(guid);
            hash = Fold(hash, guid);
        }
        else
        {
            hash = FoldByte(hash, 0x00); // "untenanted" — its own key
        }

        // Reinterpret the unsigned hash as the signed bigint pg_advisory_xact_lock takes.
        return unchecked((long)hash);

        static ulong Fold(ulong h, ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                h = FoldByte(h, b);
            }
            return h;
        }

        static ulong FoldByte(ulong h, byte b) => unchecked((h ^ b) * prime);
    }

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
/// <see cref="Claimed"/> lets the caller dispatch; the other two both leave the
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
}

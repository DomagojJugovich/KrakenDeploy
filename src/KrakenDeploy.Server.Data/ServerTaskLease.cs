using System.Globalization;
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
    /// Returns <c>false</c> when the row was not <c>Queued</c> anymore — already
    /// claimed by another wake-up, or cancelled; the caller must bail without
    /// dispatching.
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        KrakenDbContext db, Guid taskId, TimeProvider time, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
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
        return rows == 1;
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

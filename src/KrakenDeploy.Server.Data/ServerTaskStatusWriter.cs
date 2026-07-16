using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data;

/// <summary>
/// B5 — the single write path for <see cref="ServerTask"/> status transitions.
/// <para>
/// Every terminal (and status-invariant) write used to be a read-check-write:
/// re-read the status, bail if terminal, then <c>SaveChanges</c>. The guard was
/// correct but not atomic — a cancel landing between the check and the save was
/// silently overwritten. With the xmin concurrency token (see
/// <c>ServerTaskConfiguration</c>) that lost update now surfaces as a
/// <see cref="DbUpdateConcurrencyException"/>; this helper turns it into the
/// intended semantics: reload the authoritative row, re-apply the guard, and
/// either retry the write or report the transition as refused.
/// </para>
/// <para>
/// The reload-first shape is not an optimisation choice — it is REQUIRED.
/// xmin changes on every update of the row, and two untracked writers touch it
/// constantly while a task runs: the log-sequence allocation
/// (<c>TaskLogService</c>, raw UPDATE per staged batch) and the B1 lease
/// renewal (ExecuteUpdate, every minute). A tracked entity loaded at dispatch
/// start therefore carries a stale token almost immediately; saving it
/// directly would throw on virtually every write. Reloading inside the write
/// window keeps the race surface at microseconds, and the bounded retry
/// absorbs the rare interleaved bump.
/// </para>
/// <para>
/// Writes go through <c>SaveChangesAsync</c> (not ExecuteUpdate) on purpose:
/// the <c>AuditLogInterceptor</c> emission ("Deployment.Updated" rows) and
/// <c>ModifiedUtc</c> stamping ride the change-tracker pipeline, and callers
/// (e.g. the offline result ingest) rely on their staged child rows being
/// saved atomically with the status flip.
/// </para>
/// </summary>
public static class ServerTaskStatusWriter
{
    /// <summary>
    /// Attempts on a concurrency conflict before giving up. Each retry
    /// re-reads the row (fresh token + fresh status), so exhaustion means the
    /// row was concurrently updated on this many consecutive microsecond
    /// windows — treat the final exception as a genuine fault, not contention.
    /// </summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Reloads <paramref name="task"/> from the database, checks the
    /// transition guard against the authoritative status, applies
    /// <paramref name="apply"/> and saves — retrying on a concurrency
    /// conflict with a fresh reload each attempt.
    /// <para>
    /// Returns <c>false</c> without writing when the guard refuses the
    /// transition (default guard: the task is already terminal) or the row no
    /// longer exists (pruned by retention). On <c>false</c> the tracked
    /// entity holds the freshly-read database values, so
    /// <c>task.Status</c> is the authoritative state that blocked the
    /// transition. The reload resets any pending modifications on the TASK
    /// entity itself; other entities tracked by <paramref name="db"/> (staged
    /// child rows, audit entries) are untouched and get saved together with
    /// the transition.
    /// </para>
    /// </summary>
    /// <param name="db">Context tracking <paramref name="task"/>.</param>
    /// <param name="task">The tracked task instance to transition.</param>
    /// <param name="apply">Mutation to apply once the guard passes — set the
    /// new status + completion fields here and nothing else.</param>
    /// <param name="canTransitionFrom">Guard evaluated against the FRESH
    /// database status. Default: <c>!status.IsTerminal()</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> TryTransitionAsync(
        DbContext db,
        ServerTask task,
        Action<ServerTask> apply,
        Func<DeploymentStatus, bool>? canTransitionFrom = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(apply);
        canTransitionFrom ??= static status => !status.IsTerminal();

        var entry = db.Entry(task);

        for (var attempt = 1; ; attempt++)
        {
            // Refresh ORIGINAL (concurrency token) and CURRENT values from the
            // database, then mark the entity clean. GetDatabaseValues includes
            // the shadow xmin property, so the subsequent UPDATE's WHERE uses
            // the fresh token. A null result means the row is gone (retention
            // pruned it, or it never existed) — nothing to transition.
            var databaseValues = await entry.GetDatabaseValuesAsync(ct).ConfigureAwait(false);
            if (databaseValues is null)
            {
                return false;
            }
            entry.OriginalValues.SetValues(databaseValues);
            entry.CurrentValues.SetValues(databaseValues);
            entry.State = EntityState.Unchanged;

            if (!canTransitionFrom(task.Status))
            {
                return false;
            }

            apply(task);

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Someone updated the row between our reload and the save —
                // a cancel, a log-sequence bump, a lease renewal. Loop:
                // re-read, re-guard (the concurrent write may have been the
                // terminal verdict this transition must yield to), re-apply.
            }
        }
    }
}

using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// D1 Phase 2 — the ONE cancel implementation both kinds share.
/// <see cref="DeploymentService.CancelAsync"/> and
/// <see cref="RunbookService.CancelRunAsync"/> were ~40 near-identical lines
/// (T1-8 scope probe → load → B5 guarded terminal flip → B6 abort push); they
/// now both delegate here, differing only in the subtype, the operator-facing
/// noun and the pushed cancel reason.
/// </summary>
internal static class ServerTaskCanceller
{
    /// <summary>
    /// Transitions a non-terminal task to <see cref="DeploymentStatus.Cancelled"/>
    /// (B5 guarded write — a finalize landing in the window is never overwritten,
    /// and xmin churn from log/lease bumps never surfaces as a spurious error),
    /// clears <c>ScheduledFor</c> so the dispatch job can never resurrect it, and
    /// best-effort pushes the abort to the connected agent(s) AFTER the verdict is
    /// durable (B6 — an offline agent degrades to wave-boundary semantics, never
    /// to a lost cancel). Returns the updated task, or <c>null</c> when it does
    /// not exist (or is outside the active Space); throws
    /// <see cref="InvalidOperationException"/> when it is already terminal.
    /// </summary>
    internal static async Task<TTask?> CancelAsync<TTask>(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IPermissionEvaluator permissions,
        TimeProvider time,
        IAgentCancelPusher? cancelPusher,
        Guid id,
        CallerAuthorization caller,
        string taskNoun,
        string pushReason,
        CancellationToken ct)
        where TTask : ServerTask
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // T1-8: cancelling THIS task (TaskCancel) is scoped to its
        // project/environment/tenant — a TaskCancel grant restricted to Env=Test
        // must not abort a running Prod task. Strict; resolve filter-free so a
        // foreign task id fails closed. System (internal) callers skip.
        if (!caller.IsSystem)
        {
            var s = await db.Set<TTask>().IgnoreQueryFilters()
                .Where(t => t.Id == id)
                .Select(t => new { t.SpaceId, t.ProjectId, t.EnvironmentId, t.TenantId })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            await permissions.EnsureScopedAsync(
                caller, Permission.TaskCancel,
                new PermissionScope(
                    SpaceId: s?.SpaceId, ProjectId: s?.ProjectId,
                    EnvironmentId: s?.EnvironmentId, TenantId: s?.TenantId), ct)
                .ConfigureAwait(false);
        }

        var task = await db.Set<TTask>()
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        var cancelled = await ServerTaskStatusWriter.TryTransitionAsync(
            db, task, t =>
            {
                t.Status       = DeploymentStatus.Cancelled;
                t.CompletedUtc = time.GetUtcNow();
                // Belt-and-braces: a future-dated task sits Queued with a
                // ScheduledFor; the flip to Cancelled already excludes it from the
                // dispatch job's Status==Queued re-queue — clear the schedule too
                // so it can never be resurrected.
                t.ScheduledFor = null;
                // B1: terminal — release the dispatch lease (hygiene; the
                // reconciler only ever looks at Running rows).
                t.ClaimedBy    = null;
                t.LeaseUntil   = null;
            }, ct: ct).ConfigureAwait(false);
        if (!cancelled)
        {
            throw new InvalidOperationException(
                $"{taskNoun} {id} is already in a terminal state " +
                $"({task.Status}) and cannot be cancelled.");
        }

        if (cancelPusher is not null)
        {
            await cancelPusher.PushCancelAsync(id, pushReason, ct)
                .ConfigureAwait(false);
        }
        return task;
    }
}

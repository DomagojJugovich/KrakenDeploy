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

        // WP3 — close any manual-intervention gate this task was waiting on. Leaving it
        // Pending meant the detail page kept offering Approve/Reject on a CANCELLED
        // deployment and the response was accepted, writing an InterventionApproved
        // audit row naming a real person for a change that never ran; and the minutely
        // timeout sweeper would later emit InterventionTimedOut on the same dead task.
        // Cancelled is deliberately NOT a decision (see InterruptionStatusExtensions):
        // it resumes nothing and is never audited as an approval or a refusal — the
        // cancel itself is already audited.
        //
        // STAGED on the tracked context BEFORE the transition, not issued as a separate
        // ExecuteUpdate after it (WP3-b): TryTransitionAsync ends in
        // db.SaveChangesAsync, so this rides the SAME transaction. As two statements a
        // crash — or just the request's CancellationToken firing when the operator
        // navigated away — left the task durably Cancelled with its gate still Pending,
        // and nothing could ever close it: the timeout sweeper skips a non-Paused task,
        // RespondAsync refuses a terminal one, and a retry of cancel throws above before
        // reaching the close. Staging also means a REFUSED transition (already terminal,
        // returns false before saving) leaves the gate untouched, which is correct.
        var closedLabel = $"System ({taskNoun.ToLowerInvariant()} cancelled)";
        var closedUtc = time.GetUtcNow();
        var openGates = await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.TaskId == id && i.Status == InterruptionStatus.Pending)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var gate in openGates)
        {
            gate.Status         = InterruptionStatus.Cancelled;
            gate.ActedUtc       = closedUtc;
            gate.ActedByDisplay = closedLabel;
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
                // WP3: a terminal task never carries a resume checkpoint. This is the
                // third terminal writer; the other two (DeploymentWorker's finalisation
                // and FailAsync) already cleared it, and skipping it here left a
                // DEK-encrypted blob of captured SENSITIVE output values on a finished
                // row indefinitely, which DekRotationWalk then re-encrypted on every
                // rotation forever.
                t.PauseCheckpointEncrypted = null;
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

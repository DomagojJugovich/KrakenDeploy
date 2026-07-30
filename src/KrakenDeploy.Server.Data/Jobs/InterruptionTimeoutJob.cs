using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// WP3 — Hangfire recurring job (minutely) that auto-fails manual-intervention gates
/// nobody answered before <see cref="Interruption.ExpiresUtc"/>.
/// <para>
/// This is not merely a convenience. A paused task HOLDS its F1
/// <c>(project, environment, tenant)</c> slot (see
/// <see cref="DeploymentStatusExtensions.InFlightAfterClaim"/>), so a gate nobody ever
/// answers would block every future deployment of that project+environment
/// indefinitely. The timeout is what bounds that hold.
/// </para>
/// <para>
/// Expiry is treated exactly like a rejection: the task resumes only far enough to run
/// its <c>Failure</c>/<c>Always</c> cleanup steps, then finalises <c>Failed</c>. The
/// distinct <c>*.InterventionTimedOut</c> audit event (not
/// <c>*.InterventionRejected</c>) is what tells a reviewer that nobody responded rather
/// than that somebody refused — different operational follow-up.
/// </para>
/// <para>
/// Registered per-account via the same fan-out as the dispatch reconciler, so the
/// resume wake-up it enqueues carries the right <c>AccountId</c> (house rule 5). The
/// conditional <c>Pending → TimedOut</c> update in
/// <see cref="InterruptionService.ExpireAsync"/> means a human answering in the same
/// minute always wins; this job then no-ops on that row.
/// </para>
/// </summary>
public sealed class InterruptionTimeoutJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    InterruptionService interruptions,
    TimeProvider time,
    ILogger<InterruptionTimeoutJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = time.GetUtcNow();

        // Read-then-act: the ids come from the partial index
        // (ix_interruptions_pending_expiry), and ExpireAsync re-guards each one with a
        // conditional UPDATE, so a crash between the read and the writes strands
        // nothing — the next minute picks up whatever is left.
        //
        // Only gates on a still-answerable task: a terminal task's gates are closed by
        // ServerTaskCanceller, but a reconciler-interrupted one can leave a Pending row
        // behind, and expiring that would emit an InterventionTimedOut audit event —
        // which subscriptions notify on — for a deployment that failed days ago.
        var expiredIds = await db.Interruptions
            .IgnoreQueryFilters()
            .Where(i => i.Status == InterruptionStatus.Pending
                     && i.ExpiresUtc != null
                     && i.ExpiresUtc <= now
                     && i.Task.Status == DeploymentStatus.Paused)
            .Select(i => i.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (expiredIds.Count == 0)
        {
            return;
        }

        var expired = 0;
        foreach (var id in expiredIds)
        {
            if (await interruptions.ExpireAsync(id, ct).ConfigureAwait(false))
            {
                expired++;
            }
        }

        if (expired > 0)
        {
            logger.LogWarning(
                "Interruption timeout sweep: {Expired} of {Candidates} manual-intervention " +
                "gate(s) expired unanswered; their tasks will fail after running cleanup steps.",
                expired, expiredIds.Count);
        }
    }
}

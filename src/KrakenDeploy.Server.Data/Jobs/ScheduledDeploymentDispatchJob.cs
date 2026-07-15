using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: dispatches deployments whose <c>ScheduledFor</c>
/// time has arrived.
/// <para>
/// Deployments created with a future <c>ScheduledFor</c> are persisted in
/// <c>Queued</c> state but NOT written to the dispatch channel by
/// <c>DeploymentService.CreateAsync</c>. This job polls every minute and writes
/// due IDs to the in-process channel — a pure, idempotent WAKE-UP with no state
/// change of its own (B1). Exactly-once execution is the worker's atomic claim
/// (<see cref="ServerTaskLease.TryClaimAsync"/>), which also clears
/// <c>ScheduledFor</c>; until a wake-up is claimed the row simply stays due and
/// gets re-signalled next tick. The previous design cleared <c>ScheduledFor</c>
/// here BEFORE the channel writes — a crash between the two stranded the rows
/// as <c>Queued, ScheduledFor=null</c>, invisible to every query, forever.
/// </para>
/// </summary>
public sealed class ScheduledDeploymentDispatchJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    Channel<TenantWorkItem> deploymentQueue,
    TimeProvider time,
    IAccountContext accountContext,
    ILogger<ScheduledDeploymentDispatchJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = time.GetUtcNow();

        // Load IDs of DEPLOYMENTS whose scheduled time has passed. This job feeds
        // the deployment queue, so it must stay deployment-kind only — runbook-run
        // scheduling arrives with the DeploymentWorker/RunbookRunWorker merge (until
        // then runbook triggers never set ScheduledFor). IgnoreQueryFilters —
        // dispatch is space-agnostic.
        var dueIds = await db.Deployments
            .IgnoreQueryFilters()
            .Where(d => d.Status == DeploymentStatus.Queued
                     && d.ScheduledFor != null
                     && d.ScheduledFor <= now)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (dueIds.Count == 0)
        {
            return;
        }

        // Write IDs to the in-process dispatch channel. DeploymentWorker picks
        // them up and runs them exactly as it would an immediately-dispatched one;
        // its atomic claim de-duplicates overlapping job runs / retries (a losing
        // wake-up is a logged no-op). Runs inside the per-account fan-out
        // (WithAccount) in multi-account mode, so CurrentAccountId is this
        // account; Guid.Empty in single-instance mode.
        var accountId = accountContext.IsResolved ? accountContext.CurrentAccountId : Guid.Empty;
        foreach (var id in dueIds)
        {
            await deploymentQueue.Writer
                .WriteAsync(new TenantWorkItem(accountId, id), ct)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "ScheduledDeploymentDispatch: enqueued {Count} deployment(s).",
            dueIds.Count);
    }
}

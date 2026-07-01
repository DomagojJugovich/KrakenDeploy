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
/// <c>DeploymentService.CreateAsync</c>. This job polls every minute, claims
/// overdue entries by clearing their <c>ScheduledFor</c> field (preventing
/// double-dispatch if the job overlaps), then writes their IDs to the
/// in-process <see cref="Channel{Guid}"/> so <c>DeploymentWorker</c> picks
/// them up normally.
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

        // Load IDs of deployments whose scheduled time has passed.
        // IgnoreQueryFilters — dispatch is space-agnostic.
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

        // Atomically clear ScheduledFor so a concurrent run of this job (or a
        // quick retry) cannot enqueue the same deployment a second time.
        await db.Deployments
            .IgnoreQueryFilters()
            .Where(d => dueIds.Contains(d.Id))
            .ExecuteUpdateAsync(s =>
                s.SetProperty(d => d.ScheduledFor, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);

        // Write IDs to the in-process dispatch channel.  DeploymentWorker picks
        // them up and runs them exactly as it would an immediately-dispatched one.
        // Runs inside the per-account fan-out (WithAccount) in multi-account mode, so
        // CurrentAccountId is this account; Guid.Empty in single-instance mode.
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

using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job: marks <see cref="TargetStatus.Online"/> targets as
/// <see cref="TargetStatus.Offline"/> when their <c>LastSeenUtc</c> is older than
/// <see cref="OfflineThreshold"/>.
/// <para>
/// This catches agents that went silent after a server restart (when the
/// in-process grace-period task in <c>AgentHub.OnDisconnectedAsync</c> was lost).
/// Runs every 5 minutes.
/// </para>
/// </summary>
public sealed class AgentLastSeenOfflineJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TimeProvider time,
    ILogger<AgentLastSeenOfflineJob> logger)
{
    /// <summary>
    /// How long without a heartbeat before a target is considered offline.
    /// Should be longer than the SignalR 30-second grace period in AgentHub
    /// to avoid false positives during transient connectivity blips.
    /// </summary>
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(3);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var cutoff = time.GetUtcNow() - OfflineThreshold;

        // IgnoreQueryFilters so we see targets across ALL spaces — this job
        // must be space-agnostic since it runs without an HTTP request context.
        var stale = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .Where(t => t.Status == TargetStatus.Online && t.LastSeenUtc < cutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var target in stale)
        {
            target.Status = TargetStatus.Offline;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "AgentLastSeenOffline: marked {Count} target(s) offline " +
            "(last seen before {Cutoff:O}).",
            stale.Count, cutoff);
    }
}

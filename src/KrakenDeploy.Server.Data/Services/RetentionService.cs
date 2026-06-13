using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Prunes excess deployments after a successful deployment based on the lifecycle
/// phase retention policy.
/// </summary>
public class RetentionService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ISpaceContext spaceContext,
    ILogger<RetentionService> logger)
{
    /// <summary>
    /// Called after a deployment succeeds. Finds the lifecycle phase that owns the
    /// deployment's environment and deletes the oldest successful deployments for the same
    /// project+environment beyond the <c>RetentionKeepDeployments</c> threshold.
    /// Does nothing if no lifecycle is configured or keep is 0 (unlimited).
    /// </summary>
    public async Task PruneAfterDeploymentAsync(Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // This runs in a background DI scope (fire-and-forget from
        // AgentHub.CompleteDeploymentAsync) with no active Space → DefaultSpaceId.
        // Load the deployment filter-free so a non-Default-Space deployment is
        // still found, then scope the rest of the prune (lifecycle/project lookup,
        // success-id query, ExecuteDelete) to its Space — so we prune within the
        // deployment's own Space and never reach across Spaces.
        var deployment = await db.Deployments
            .IgnoreQueryFilters()
            .Include(d => d.Release)
                .ThenInclude(r => r.Channel)
                    .ThenInclude(c => c!.Lifecycle)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            .ConfigureAwait(false);

        if (deployment is null)
        {
            return;
        }

        using var spaceScope = spaceContext.WithSpace(deployment.SpaceId);

        var lifecycle = deployment.Release.Channel?.Lifecycle
            ?? await db.Projects
                .Where(p => p.Id == deployment.Release.ProjectId)
                .Select(p => p.Lifecycle)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        if (lifecycle is null)
        {
            return;
        }

        var phase = lifecycle.Phases.FirstOrDefault(p =>
            p.EnvironmentIds.Contains(deployment.EnvironmentId) ||
            p.OptionalEnvironmentIds.Contains(deployment.EnvironmentId));

        if (phase is null || phase.RetentionKeepDeployments == 0)
        {
            return;
        }

        var projectId = deployment.Release.ProjectId;
        var envId = deployment.EnvironmentId;
        var keep = phase.RetentionKeepDeployments;

        // IDs of successful deployments for this project+environment, newest first.
        var successIds = await db.Deployments
            .Where(d => d.Release.ProjectId == projectId &&
                        d.EnvironmentId == envId &&
                        d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.CompletedUtc)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var toDelete = successIds.Skip(keep).ToList();
        if (toDelete.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Retention: pruning {Count} old successful deployment(s) for project {ProjectId} " +
            "in environment {EnvId} (keep={Keep}).",
            toDelete.Count, projectId, envId, keep);

        await db.Deployments
            .Where(d => toDelete.Contains(d.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}

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
    /// Number of successful runbook runs kept per (runbook, environment). Runbook
    /// runs have no lifecycle phase to source a <c>RetentionKeepDeployments</c>
    /// policy from, so the keep count is fixed here for now. finish-plan WP9
    /// (retention expansion) surfaces retention as a configurable knob — a knob is
    /// deliberately NOT added here.
    /// </summary>
    public const int DefaultRunbookRunKeep = 50;

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

    /// <summary>
    /// Called after a runbook run succeeds. Deletes the oldest successful runs for the
    /// same runbook + environment beyond <see cref="DefaultRunbookRunKeep"/>, so their
    /// log children cascade away with the parent row. Mirrors
    /// <see cref="PruneAfterDeploymentAsync"/>: only exactly-<see cref="DeploymentStatus.Succeeded"/>
    /// runs are eligible, so a <c>Queued</c>/<c>Running</c> run and its live log tail
    /// are never selected. Closes the gap where runbook runs accumulated unbounded
    /// (deployments were pruned, runbook runs never were).
    /// </summary>
    public async Task PruneAfterRunbookRunAsync(
        Guid runId, int? keepOverride = null, CancellationToken ct = default)
    {
        // keepOverride lets finish-plan WP9 pass a configured value once retention
        // becomes tunable (and lets tests exercise pruning without seeding 50+ runs);
        // production callers pass nothing and get DefaultRunbookRunKeep.
        var keep = keepOverride ?? DefaultRunbookRunKeep;

        // keep <= 0 means "unlimited / disabled", matching the deployment path
        // (PruneAfterDeploymentAsync treats RetentionKeepDeployments == 0 as
        // unlimited). Without this, keep=0 would Skip(0) and delete every run — a
        // data-loss footgun once WP9 wires keepOverride to operator config.
        if (keep <= 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Background DI scope with no active Space → DefaultSpaceId. Load the run
        // filter-free so a non-Default-Space run is still found, then scope the
        // rest of the prune to its Space so we never reach across Spaces.
        var run = await db.RunbookRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            .ConfigureAwait(false);

        if (run is null)
        {
            return;
        }

        using var spaceScope = spaceContext.WithSpace(run.SpaceId);

        var runbookId = run.RunbookId;
        var envId = run.EnvironmentId;

        // IDs of successful runs for this runbook+environment, newest first.
        var successIds = await db.RunbookRuns
            .Where(r => r.RunbookId == runbookId &&
                        r.EnvironmentId == envId &&
                        r.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(r => r.CompletedUtc)
            .Select(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var toDelete = successIds.Skip(keep).ToList();
        if (toDelete.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Retention: pruning {Count} old successful runbook run(s) for runbook {RunbookId} " +
            "in environment {EnvId} (keep={Keep}).",
            toDelete.Count, runbookId, envId, keep);

        // ExecuteDelete on the TPH subtype (kind=1) DELETEs the server_tasks rows;
        // DB-level ON DELETE CASCADE removes the log/step/output children.
        await db.RunbookRuns
            .Where(r => toDelete.Contains(r.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}

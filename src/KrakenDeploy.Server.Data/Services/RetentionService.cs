using System.Text.Json;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Performance;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.ArtifactStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Prunes excess execution history + the on-disk files behind it.
/// <para>
/// Two trigger paths share one set of prune routines:
/// <list type="bullet">
///   <item><b>Event-driven</b> — <see cref="PruneAfterDeploymentAsync"/> /
///         <see cref="PruneAfterRunbookRunAsync"/> fire post-completion and prune
///         within the just-finished task's project/runbook.</item>
///   <item><b>Scheduled sweep</b> — <see cref="RunSweepAsync"/> (WP9) walks every
///         Space and applies the same deployment/runbook pruning plus release
///         pruning, package pruning (reference-protected), log age-capping, and
///         on-disk file cleanup. Supports a dry-run mode that logs the prune set
///         without deleting.</item>
/// </list>
/// </para>
/// <para>
/// WP9 closed the file-orphan gap: the row prune used <c>ExecuteDelete</c> + DB
/// cascades, which remove the <c>task_artifacts</c>/<c>server_tasks</c> rows but
/// never touch the store — leaving artifact files and offline drop-bundle zips on
/// disk forever. Both prune paths now load the affected tasks' artifact
/// <c>StoredPath</c>s and <c>DropBundlePath</c> BEFORE the delete and remove the
/// files through the store abstractions; <see cref="SweepOrphanedFilesAsync"/> is
/// the safety net for files orphaned by rows pruned before this landed.
/// </para>
/// </summary>
public class RetentionService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ISpaceContext spaceContext,
    IArtifactStore artifactStore,
    IPackageStore packageStore,
    IAccountContext accountContext,
    SettingsService settingsService,
    IConfiguration configuration,
    ILogger<RetentionService> logger)
{
    /// <summary>
    /// Fallback number of successful runbook runs kept per (runbook, environment)
    /// when neither a per-runbook <c>Runbook.RetentionKeepRuns</c> override nor a
    /// saved <see cref="PerformanceSettings.RunbookRunRetentionKeep"/> value applies
    /// (fresh install, no settings row). WP9 wired the settings knob + per-runbook
    /// override ahead of this const; it remains the last-resort default so the
    /// event-driven prune still behaves when nothing is configured.
    /// </summary>
    public const int DefaultRunbookRunKeep = 50;

    private static readonly JsonSerializerOptions RefJsonOpts = new(JsonSerializerDefaults.Web);

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
        // "Successful" spans BOTH terminal-success states: Succeeded and
        // SucceededWithWarnings (a completed deployment whose only failure was a
        // non-required step — the yellow-badge state). Both count toward the keep
        // window AND are eligible to be pruned; excluding SucceededWithWarnings
        // left those rows (and their task_step_logs/task_log_live children)
        // accumulating unbounded while never counting against the limit. This is
        // the settled terminal-success contract — finish-plan WP9 (retention
        // expansion) tunes the keep count/policy, it must NOT narrow this back to
        // Succeeded-only. Running/Queued/Failed/Cancelled/PendingOfflineResult are
        // never candidates.
        var successIds = await db.Deployments
            .Where(d => d.Release.ProjectId == projectId &&
                        d.EnvironmentId == envId &&
                        (d.Status == DeploymentStatus.Succeeded ||
                         d.Status == DeploymentStatus.SucceededWithWarnings))
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

        // WP9: delete the on-disk files BEFORE the rows so an orphan is never
        // created on this path (ExecuteDelete bypasses change tracking and would
        // otherwise drop the rows without ever seeing StoredPath/DropBundlePath).
        var files = await DeleteTaskFilesAsync(db, toDelete, ct).ConfigureAwait(false);

        await db.Deployments
            .Where(d => toDelete.Contains(d.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (files > 0)
        {
            logger.LogInformation(
                "Retention: deleted {Files} on-disk file(s) for the {Count} pruned deployment(s).",
                files, toDelete.Count);
        }
    }

    /// <summary>
    /// Called after a runbook run succeeds. Deletes the oldest successful runs for the
    /// same runbook + environment beyond the configured keep count, so their log
    /// children cascade away with the parent row. Mirrors
    /// <see cref="PruneAfterDeploymentAsync"/>: terminal-success spans BOTH
    /// <see cref="DeploymentStatus.Succeeded"/> AND
    /// <see cref="DeploymentStatus.SucceededWithWarnings"/>. D1 makes the latter
    /// reachable for runbook runs (they now honour failure modes + non-required
    /// step failures through the unified orchestrator), so counting only
    /// exactly-Succeeded would leave SucceededWithWarnings runs accumulating
    /// unbounded — the same gap the deployment path already closes. A
    /// <c>Queued</c>/<c>Running</c> run and its live log tail are never selected.
    /// </summary>
    /// <param name="keepOverride">Explicit keep count (tests, or a caller that has
    /// already resolved policy). When <c>null</c> the count is resolved from the
    /// runbook's <c>RetentionKeepRuns</c> override, then the instance-wide
    /// <see cref="PerformanceSettings.RunbookRunRetentionKeep"/>, then
    /// <see cref="DefaultRunbookRunKeep"/>.</param>
    public async Task PruneAfterRunbookRunAsync(
        Guid runId, int? keepOverride = null, CancellationToken ct = default)
    {
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

        var keep = keepOverride ?? await ResolveRunbookRunKeepAsync(db, run.RunbookId, ct)
            .ConfigureAwait(false);

        // keep <= 0 means "unlimited / disabled", matching the deployment path
        // (PruneAfterDeploymentAsync treats RetentionKeepDeployments == 0 as
        // unlimited). Without this, keep=0 would Skip(0) and delete every run — a
        // data-loss footgun now that the keep count is operator-configurable.
        if (keep <= 0)
        {
            return;
        }

        var runbookId = run.RunbookId;
        var envId = run.EnvironmentId;

        // IDs of successful runs for this runbook+environment, newest first.
        // Terminal-success = Succeeded OR SucceededWithWarnings (D1: runbook runs
        // now reach the yellow-badge state via the unified orchestrator's failure
        // modes). Both count toward the keep window AND are eligible to be pruned.
        var successIds = await db.RunbookRuns
            .Where(r => r.RunbookId == runbookId &&
                        r.EnvironmentId == envId &&
                        (r.Status == DeploymentStatus.Succeeded ||
                         r.Status == DeploymentStatus.SucceededWithWarnings))
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

        // WP9: delete on-disk files before the rows (runbook runs can carry
        // artifacts too). See PruneAfterDeploymentAsync for the why.
        var files = await DeleteTaskFilesAsync(db, toDelete, ct).ConfigureAwait(false);

        // ExecuteDelete on the TPH subtype (kind=1) DELETEs the server_tasks rows;
        // DB-level ON DELETE CASCADE removes the log/step/output children.
        await db.RunbookRuns
            .Where(r => toDelete.Contains(r.Id))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (files > 0)
        {
            logger.LogInformation(
                "Retention: deleted {Files} on-disk file(s) for the {Count} pruned runbook run(s).",
                files, toDelete.Count);
        }
    }

    // ── Scheduled sweep (WP9) ─────────────────────────────────────────────────

    /// <summary>
    /// The scheduled retention sweep. Walks every Space (so it works from a
    /// background scope with no active Space) and applies, per Space: deployment
    /// pruning, release pruning, reference-protected package pruning, runbook-run
    /// pruning, step-log age-capping, the orphan task_log_live sweep, and on-disk
    /// file cleanup. In <see cref="RetentionSweepOptions.DryRun"/> mode it computes
    /// and returns the full prune set but deletes nothing.
    /// <para>
    /// Order matters: deployments first (so a release's deployments are gone before
    /// the release-prune checks for references), then releases, then packages (so
    /// the package reference-guard sees the post-prune release/deployment set).
    /// </para>
    /// </summary>
    public async Task<RetentionSweepResult> RunSweepAsync(
        RetentionSweepOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var spaceIds = await ListSpaceIdsAsync(ct).ConfigureAwait(false);
        var total = new RetentionSweepResult { DryRun = options.DryRun };
        
        // Per-Space sweeps: deployments, releases, packages, runbook runs, logs
        foreach (var spaceId in spaceIds)
        {
            ct.ThrowIfCancellationRequested();
            using var spaceScope = spaceContext.WithSpace(spaceId);
            var perSpace = await SweepSpaceAsync(options, ct).ConfigureAwait(false);
            total += perSpace;
        }

        // Orphaned file sweep runs ONCE (not per-Space) because the artifact and
        // drop-bundle roots are account-scoped / global, shared across all Spaces.
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var orphanFiles = await SweepOrphanedFilesAsync(db, options.DryRun, ct).ConfigureAwait(false);
        total += new RetentionSweepResult
        {
            DryRun          = options.DryRun,
            ArtifactFiles   = orphanFiles.ArtifactFiles,
            DropBundleFiles = orphanFiles.DropBundleFiles,
        };

        logger.LogInformation(
            "Retention sweep ({Mode}) complete: {Summary}.",
            options.DryRun ? "dry-run" : "apply", total.ToSummary());
        return total;
    }

    private async Task<RetentionSweepResult> SweepSpaceAsync(
        RetentionSweepOptions options, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var deployments = await SweepDeploymentsAsync(db, options.DryRun, ct).ConfigureAwait(false);
        var releases    = await SweepReleasesAsync(db, options.DryRun, ct).ConfigureAwait(false);
        var packages    = await SweepPackagesAsync(db, options, ct).ConfigureAwait(false);
        var runs        = await SweepRunbookRunsAsync(db, options, ct).ConfigureAwait(false);
        var logs        = await SweepLogsAsync(db, options, ct).ConfigureAwait(false);

        return new RetentionSweepResult
        {
            DryRun          = options.DryRun,
            Deployments     = deployments,
            Releases        = releases,
            Packages        = packages,
            RunbookRuns     = runs,
            StepLogBlobs    = logs.StepLogBlobs,
            OrphanLiveLogs  = logs.OrphanLiveLogs,
        };
    }

    /// <summary>
    /// Deployment pruning across the whole Space: for every (project, environment)
    /// that has a lifecycle phase keep-window, prune successful deployments beyond
    /// the window. Reuses the exact terminal-success contract + file deletion of the
    /// event-driven path. Returns the number of deployments pruned (or that would
    /// be pruned in dry-run).
    /// </summary>
    private async Task<int> SweepDeploymentsAsync(
        KrakenDbContext db, bool dryRun, CancellationToken ct)
    {
        // Group successful deployments by (project, environment); resolve each
        // group's keep-window from the lifecycle phase that owns the environment.
        // Group on the release's ProjectId (the same source the event-driven
        // PruneAfterDeploymentAsync uses) rather than the denormalized task column,
        // so the two paths agree on the project even if the denormalized value is
        // absent.
        var groups = await db.Deployments
            .Where(d => d.Status == DeploymentStatus.Succeeded ||
                        d.Status == DeploymentStatus.SucceededWithWarnings)
            .GroupBy(d => new { ProjectId = d.Release.ProjectId, d.EnvironmentId })
            .Select(g => new { g.Key.ProjectId, g.Key.EnvironmentId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var pruned = 0;
        foreach (var group in groups)
        {
            var keep = await ResolveDeploymentKeepAsync(db, group.ProjectId, group.EnvironmentId, ct)
                .ConfigureAwait(false);
            if (keep <= 0)
            {
                continue;
            }

            var successIds = await db.Deployments
                .Where(d => d.Release.ProjectId == group.ProjectId &&
                            d.EnvironmentId == group.EnvironmentId &&
                            (d.Status == DeploymentStatus.Succeeded ||
                             d.Status == DeploymentStatus.SucceededWithWarnings))
                .OrderByDescending(d => d.CompletedUtc)
                .Select(d => d.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var toDelete = successIds.Skip(keep).ToList();
            if (toDelete.Count == 0)
            {
                continue;
            }

            pruned += toDelete.Count;
            if (dryRun)
            {
                continue;
            }

            await DeleteTaskFilesAsync(db, toDelete, ct).ConfigureAwait(false);
            await db.Deployments
                .Where(d => toDelete.Contains(d.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return pruned;
    }

    /// <summary>
    /// Release pruning (WP9 item 1, Octopus semantics): a release is prunable when
    /// it falls outside every lifecycle phase's release keep-window AND has no
    /// retained deployments. Implemented as: keep the newest
    /// <c>max(phase.RetentionKeepReleases)</c> releases per project (the
    /// keep-window), and among the rest delete only those with ZERO deployments
    /// (a release with any deployment is pinned by execution history — the
    /// <c>server_tasks.release_id</c> RESTRICT FK would refuse the delete anyway).
    /// Deletion goes through the shared <see cref="ReleaseService.DeleteCoreAsync"/>
    /// path (the same guard WP5's manual delete uses). Returns the prune count.
    /// </summary>
    private async Task<int> SweepReleasesAsync(
        KrakenDbContext db, bool dryRun, CancellationToken ct)
    {
        var projects = await db.Projects
            .Select(p => new { p.Id, p.LifecycleId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var pruned = 0;
        foreach (var project in projects)
        {
            if (project.LifecycleId is not { } lifecycleId)
            {
                continue;
            }

            var keep = await db.Lifecycles
                .Where(l => l.Id == lifecycleId)
                .Select(l => l.Phases)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false) is { Count: > 0 } phases
                ? phases.Max(p => p.RetentionKeepReleases)
                : 0;

            // keep == 0 → release pruning disabled for this lifecycle (opt-in),
            // mirroring RetentionKeepDeployments == 0 → unlimited.
            if (keep <= 0)
            {
                continue;
            }

            var releaseIds = await db.Releases
                .Where(r => r.ProjectId == project.Id)
                .OrderByDescending(r => r.CreatedUtc)
                .Select(r => r.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var candidates = releaseIds.Skip(keep).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            // Only releases with NO deployments are prunable — execution history
            // pins its releases (RESTRICT FK). This is the "no retained deployments"
            // half of the Octopus rule; the keep-window skip above is the other half.
            var referenced = await db.Deployments
                .Where(d => candidates.Contains(d.ReleaseId))
                .Select(d => d.ReleaseId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var deletable = candidates.Where(id => !referenced.Contains(id)).ToList();
            if (deletable.Count == 0)
            {
                continue;
            }

            if (dryRun)
            {
                pruned += deletable.Count;
                continue;
            }

            // Shared delete path with WP5's manual release delete. The RESTRICT FK
            // guard is re-asserted inside DeleteCoreAsync (defence in depth — the
            // referenced-set check above is a pre-filter, not the authority).
            // Wrap in try-catch: a race (deployment created between check and delete)
            // or data inconsistency should log and continue, not abort the whole sweep.
            // Increment count per successful delete so the audit log reflects reality.
            foreach (var releaseId in deletable)
            {
                try
                {
                    await ReleaseService.DeleteCoreAsync(db, releaseId, ct).ConfigureAwait(false);
                    pruned++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Retention sweep: failed to delete release {ReleaseId} (likely referenced by a deployment created after the pre-check).",
                        releaseId);
                }
            }
        }

        return pruned;
    }

    /// <summary>
    /// Package pruning (WP9 item 2): keep the newest
    /// <see cref="RetentionSweepOptions.PackageKeepVersions"/> versions per package
    /// id, but NEVER delete a version pinned by a protected release's
    /// <c>ProcessSnapshot</c> (primary or referenced package) — which, because every
    /// release with a deployment is protected, also covers "referenced by a retained
    /// deployment". Returns the prune count.
    /// <para>
    /// Pruning deployments already revokes historical AgentPackageEntitlement (the
    /// entitlement is computed from TaskTargetAssignment → release snapshot, so a
    /// pruned deployment stops contributing) — known and accepted; this path does
    /// not manage entitlements.
    /// </para>
    /// </summary>
    private async Task<int> SweepPackagesAsync(
        KrakenDbContext db, RetentionSweepOptions options, CancellationToken ct)
    {
        if (options.PackageKeepVersions <= 0)
        {
            return 0;
        }

        var (protectedVersions, protectedPackageIds) = await BuildProtectedPackageVersionsAsync(db, ct).ConfigureAwait(false);

        var packageIds = await db.Packages
            .Select(p => p.PackageId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var pruned = 0;
        foreach (var packageId in packageIds)
        {
            // If the entire package ID is protected (unresolved "latest" pin), skip all pruning
            if (protectedPackageIds.Contains(packageId))
            {
                continue;
            }

            var versions = await db.Packages
                .Where(p => p.PackageId == packageId)
                .OrderByDescending(p => p.UploadedUtc)
                .Select(p => new { p.Id, p.Version, p.StoredPath })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var toDelete = versions
                .Skip(options.PackageKeepVersions)
                .Where(v => !protectedVersions.Contains((packageId, v.Version)))
                .ToList();
            if (toDelete.Count == 0)
            {
                continue;
            }

            pruned += toDelete.Count;
            if (options.DryRun)
            {
                continue;
            }

            // Best-effort per file — a locked/missing file must not abort the sweep
            // (mirrors DeleteTaskFilesAsync). The DB row delete below still proceeds
            // so the row doesn't linger forever; the orphan-file sweep is the backstop.
            foreach (var v in toDelete)
            {
                try
                {
                    await packageStore.DeleteAsync(v.StoredPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Retention sweep: failed to delete package file {Path}.", v.StoredPath);
                }
            }

            var ids = toDelete.Select(v => v.Id).ToList();
            await db.Packages
                .Where(p => ids.Contains(p.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return pruned;
    }

    /// <summary>
    /// Runbook-run pruning across the whole Space: for every (runbook, environment)
    /// prune successful runs beyond the resolved keep count (per-runbook override →
    /// instance default). Returns the prune count.
    /// </summary>
    private async Task<int> SweepRunbookRunsAsync(
        KrakenDbContext db, RetentionSweepOptions options, CancellationToken ct)
    {
        var groups = await db.RunbookRuns
            .Where(r => r.Status == DeploymentStatus.Succeeded ||
                        r.Status == DeploymentStatus.SucceededWithWarnings)
            .GroupBy(r => new { r.RunbookId, r.EnvironmentId })
            .Select(g => new { g.Key.RunbookId, g.Key.EnvironmentId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var pruned = 0;
        foreach (var group in groups)
        {
            var keep = await ResolveRunbookRunKeepAsync(
                    db, group.RunbookId, options.RunbookRunKeep, ct)
                .ConfigureAwait(false);
            if (keep <= 0)
            {
                continue;
            }

            var successIds = await db.RunbookRuns
                .Where(r => r.RunbookId == group.RunbookId &&
                            r.EnvironmentId == group.EnvironmentId &&
                            (r.Status == DeploymentStatus.Succeeded ||
                             r.Status == DeploymentStatus.SucceededWithWarnings))
                .OrderByDescending(r => r.CompletedUtc)
                .Select(r => r.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var toDelete = successIds.Skip(keep).ToList();
            if (toDelete.Count == 0)
            {
                continue;
            }

            pruned += toDelete.Count;
            if (options.DryRun)
            {
                continue;
            }

            await DeleteTaskFilesAsync(db, toDelete, ct).ConfigureAwait(false);
            await db.RunbookRuns
                .Where(r => toDelete.Contains(r.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return pruned;
    }

    /// <summary>
    /// Log pruning (WP9 item 5): age-cap <c>task_step_logs</c> blob rows whose parent
    /// task completed more than <see cref="RetentionSweepOptions.TaskLogAgeDays"/> ago,
    /// and sweep orphaned <c>task_log_live</c> staging rows whose parent task is already
    /// terminal (they should have been compacted into a step-log blob at completion —
    /// a remainder is an orphan). Returns both counts.
    /// </summary>
    private static async Task<(int StepLogBlobs, int OrphanLiveLogs)> SweepLogsAsync(
        KrakenDbContext db, RetentionSweepOptions options, CancellationToken ct)
    {
        var stepLogBlobs = 0;
        if (options.TaskLogAgeDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.TaskLogAgeDays);
            if (options.DryRun)
            {
                // Dry-run: count matching rows
                stepLogBlobs = await db.TaskStepLogs
                    .CountAsync(l => l.Task.CompletedUtc != null && l.Task.CompletedUtc < cutoff, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // Non-dry-run: delete directly with predicate (avoids materializing unbounded ID list)
                stepLogBlobs = await db.TaskStepLogs
                    .Where(l => l.Task.CompletedUtc != null && l.Task.CompletedUtc < cutoff)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
            }
        }

        // Orphaned live-log staging rows: a terminal task should have no live tail
        // (the compactor moves completed-step lines into task_step_logs and sweeps
        // the remainder at terminal status). Any live row on a terminal task is an
        // orphan — sweep regardless of the age knob. Add a minimum-age guard (1 hour)
        // to avoid racing with the compactor on recently-completed tasks.
        // Predicate-based count/delete (like the step-log path above) so a large
        // orphan backlog doesn't materialize an unbounded ID list + IN(...) clause.
        var compactionGracePeriod = DateTimeOffset.UtcNow.AddHours(-1);
        int orphanLiveCount;
        if (options.DryRun)
        {
            orphanLiveCount = await db.TaskLogLive
                .CountAsync(l => (l.Task.Status == DeploymentStatus.Succeeded ||
                                  l.Task.Status == DeploymentStatus.SucceededWithWarnings ||
                                  l.Task.Status == DeploymentStatus.Failed ||
                                  l.Task.Status == DeploymentStatus.Cancelled) &&
                                 l.Task.CompletedUtc != null &&
                                 l.Task.CompletedUtc < compactionGracePeriod, ct)
                .ConfigureAwait(false);
        }
        else
        {
            orphanLiveCount = await db.TaskLogLive
                .Where(l => (l.Task.Status == DeploymentStatus.Succeeded ||
                             l.Task.Status == DeploymentStatus.SucceededWithWarnings ||
                             l.Task.Status == DeploymentStatus.Failed ||
                             l.Task.Status == DeploymentStatus.Cancelled) &&
                            l.Task.CompletedUtc != null &&
                            l.Task.CompletedUtc < compactionGracePeriod)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return (stepLogBlobs, orphanLiveCount);
    }

    /// <summary>
    /// Safety-net file sweep (WP9 item 4): deletes on-disk artifact directories and
    /// drop-bundle zips whose owning task row no longer exists — the files orphaned
    /// by rows pruned before inline file deletion landed (or by any crash between a
    /// row delete and its file delete). Runs even in dry-run (counting only). The
    /// artifact root is account-scoped, so in multi-account mode this only sees the
    /// active account's tree (the sweep runs inside the account scope).
    /// </summary>
    private async Task<(int ArtifactFiles, int DropBundleFiles)> SweepOrphanedFilesAsync(
        KrakenDbContext db, bool dryRun, CancellationToken ct)
    {
        var artifactFiles = 0;
        var dropBundleFiles = 0;

        // ── Artifact directories: {artifactRoot}/{taskId:N}/ ──────────────────
        var artifactRoot = ResolveArtifactRoot();
        if (Directory.Exists(artifactRoot))
        {
            // Enumerate directories first (bounded by on-disk count, not DB rows),
            // then batch-check which task IDs exist in the database.
            var dirTaskIds = new List<Guid>();
            var dirPaths = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(artifactRoot))
            {
                var name = Path.GetFileName(dir);
                if (Guid.TryParseExact(name, "N", out var taskId))
                {
                    dirTaskIds.Add(taskId);
                    dirPaths.Add(dir);
                }
            }

            // Batch-query the database for existing task IDs (chunked to avoid
            // overly large IN clauses). Use IgnoreQueryFilters() because the artifact
            // root is account-scoped (shared across all Spaces), so we must check
            // liveness against ALL tasks, not just the current Space's tasks.
            var liveSet = new HashSet<Guid>();
            const int batchSize = 1000;
            for (int i = 0; i < dirTaskIds.Count; i += batchSize)
            {
                var batch = dirTaskIds.Skip(i).Take(batchSize).ToList();
                var existing = await db.ServerTasks
                    .IgnoreQueryFilters()
                    .Where(t => batch.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var id in existing)
                {
                    liveSet.Add(id);
                }
            }

            // Delete directories whose task IDs don't exist in the database.
            for (int i = 0; i < dirPaths.Count; i++)
            {
                if (liveSet.Contains(dirTaskIds[i]))
                {
                    continue;
                }

                artifactFiles++;
                if (!dryRun)
                {
                    try { Directory.Delete(dirPaths[i], recursive: true); }
                    catch (Exception ex) { logger.LogWarning(ex,
                        "Retention sweep: failed to delete orphaned artifact dir {Dir}.", dirPaths[i]); }
                }
            }
        }

        // ── Drop-bundle zips: {dataPath}/drop-bundles/{taskId}/drop-{taskId}.zip ──
        // Drop-bundles live under dataPath (NOT account-scoped), and the bundle root
        // is shared across all Spaces — so the referenced-path query must also be
        // global (IgnoreQueryFilters), matching the artifact liveness check above.
        var dataPath = configuration["Server:DataPath"] ?? "data";
        var bundleRoot = Path.Combine(dataPath, "drop-bundles");
        if (Directory.Exists(bundleRoot))
        {
            var referencedPaths = await db.ServerTasks
                .IgnoreQueryFilters()
                .Where(t => t.DropBundlePath != null)
                .Select(t => t.DropBundlePath!)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            
            // Defensive containment check: only consider a path as "referenced" if it
            // actually resolves under bundleRoot. This makes the logic fail-safe rather
            // than fail-open if DropBundlePath is ever stored in an unexpected format.
            // Use OrdinalIgnoreCase to match Windows NTFS case-insensitivity and the
            // OrdinalIgnoreCase StartsWith check below.
            var referencedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in referencedPaths)
            {
                var fullPath = Path.Combine(dataPath, p.Replace('/', Path.DirectorySeparatorChar));
                var parentDir = Path.GetDirectoryName(fullPath);
                
                // Only add if the parent directory is actually under bundleRoot
                if (parentDir != null && 
                    parentDir.StartsWith(bundleRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var dirName = Path.GetFileName(parentDir);
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        referencedDirs.Add(dirName);
                    }
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(bundleRoot))
            {
                var name = Path.GetFileName(dir);
                if (referencedDirs.Contains(name))
                {
                    continue;
                }

                dropBundleFiles++;
                if (!dryRun)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) { logger.LogWarning(ex,
                        "Retention sweep: failed to delete orphaned drop-bundle dir {Dir}.", dir); }
                }
            }
        }

        return (artifactFiles, dropBundleFiles);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the on-disk artifact files + offline drop-bundle zips owned by the
    /// given tasks, BEFORE their rows are deleted. Returns the number of files
    /// removed. Best-effort per file (a missing/locked file must not fail the prune
    /// — the orphan sweep is the backstop).
    /// </summary>
    private async Task<int> DeleteTaskFilesAsync(
        KrakenDbContext db, List<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        var artifactPaths = await db.TaskArtifacts
            .Where(a => taskIds.Contains(a.TaskId))
            .Select(a => a.StoredPath)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dropBundlePaths = await db.ServerTasks
            .Where(t => taskIds.Contains(t.Id) && t.DropBundlePath != null)
            .Select(t => t.DropBundlePath!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var deleted = 0;
        foreach (var storedPath in artifactPaths)
        {
            try { artifactStore.Delete(storedPath); deleted++; }
            catch (Exception ex) { logger.LogWarning(ex,
                "Retention: failed to delete artifact file {Path}.", storedPath); }
        }

        var dataPath = configuration["Server:DataPath"] ?? "data";
        foreach (var bundlePath in dropBundlePaths)
        {
            try
            {
                var full = Path.Combine(dataPath, bundlePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    File.Delete(full);
                    deleted++;
                }
                // Also remove the now-empty per-deployment bundle directory.
                var dir = Path.GetDirectoryName(full);
                if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex,
                "Retention: failed to delete drop-bundle file {Path}.", bundlePath); }
        }

        return deleted;
    }

    /// <summary>
    /// Resolves the deployment keep-window for a (project, environment): the
    /// <c>RetentionKeepDeployments</c> of the lifecycle phase that owns the environment.
    /// Returns 0 when no lifecycle/phase applies (→ unlimited, no pruning).
    /// </summary>
    private static async Task<int> ResolveDeploymentKeepAsync(
        KrakenDbContext db, Guid projectId, Guid environmentId, CancellationToken ct)
    {
        var lifecycle = await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Lifecycle)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (lifecycle is null)
        {
            return 0;
        }

        var phase = lifecycle.Phases.FirstOrDefault(p =>
            p.EnvironmentIds.Contains(environmentId) ||
            p.OptionalEnvironmentIds.Contains(environmentId));

        return phase?.RetentionKeepDeployments ?? 0;
    }

    /// <summary>
    /// Resolves the runbook-run keep count: per-runbook <c>RetentionKeepRuns</c>
    /// override (any non-null value is authoritative, including 0 = keep all) →
    /// instance-wide <see cref="PerformanceSettings.RunbookRunRetentionKeep"/> (when
    /// a settings row exists) → <see cref="DefaultRunbookRunKeep"/>.
    /// </summary>
    private Task<int> ResolveRunbookRunKeepAsync(
        KrakenDbContext db, Guid runbookId, CancellationToken ct)
        => ResolveRunbookRunKeepAsync(db, runbookId, instanceDefault: null, ct);

    private async Task<int> ResolveRunbookRunKeepAsync(
        KrakenDbContext db, Guid runbookId, int? instanceDefault, CancellationToken ct)
    {
        var perRunbook = await db.Runbooks
            .Where(r => r.Id == runbookId)
            .Select(r => r.RetentionKeepRuns)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (perRunbook is not null)
        {
            return perRunbook.Value;
        }

        if (instanceDefault is not null)
        {
            return instanceDefault.Value;
        }

        var saved = await settingsService
            .TryGetAsync<PerformanceSettings>(ct: ct)
            .ConfigureAwait(false);
        return saved?.RunbookRunRetentionKeep ?? DefaultRunbookRunKeep;
    }

    /// <summary>
    /// Builds the set of (packageId, version) pairs that retention must NEVER delete:
    /// every primary + referenced package version pinned by a PROTECTED release's
    /// <c>ProcessSnapshot</c>. A release is protected when it has any deployment
    /// (execution history pins it) — so this transitively covers "referenced by a
    /// retained deployment". jsonb snapshots are not SQL-queryable, so the protected
    /// releases' snapshots are materialised and scanned in memory (the same approach
    /// as <c>AgentPackageEntitlement</c>).
    /// <para>
    /// Returns a tuple: (specific version pins, fully-protected package IDs). A package
    /// ID is fully protected when a protected release references it with no resolved
    /// version (an unresolved "latest" pin) — in that case ALL versions are protected.
    /// </para>
    /// </summary>
    private static async Task<(HashSet<(string PackageId, string Version)> ProtectedVersions, HashSet<string> ProtectedPackageIds)> BuildProtectedPackageVersionsAsync(
        KrakenDbContext db, CancellationToken ct)
    {
        var protectedSnapshots = await db.Releases
            .Where(r => r.ProcessSnapshot != null &&
                        (db.Deployments.Any(d => d.ReleaseId == r.Id)))
            .Select(r => r.ProcessSnapshot)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var protectedVersions = new HashSet<(string, string)>(PackageVersionComparer.Instance);
        var protectedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in protectedSnapshots)
        {
            foreach (var step in snapshot)
            {
                if (!string.IsNullOrEmpty(step.PackageId) && !string.IsNullOrEmpty(step.PackageVersion))
                {
                    protectedVersions.Add((step.PackageId, step.PackageVersion));
                }

                foreach (var (id, version) in ReferencedPackageVersions(step))
                {
                    if (version == "")
                    {
                        // Unresolved "latest" pin — protect ALL versions of this package ID
                        protectedPackageIds.Add(id);
                    }
                    else
                    {
                        protectedVersions.Add((id, version));
                    }
                }
            }
        }

        return (protectedVersions, protectedPackageIds);
    }

    /// <summary>
    /// Extracts the referenced (helper) package (id, version) pairs pinned in a step
    /// snapshot's <c>Config</c> under the Octopus-compatible PackageReferences key.
    /// A reference with no resolved version protects ALL versions of that package id
    /// (represented as version = "") — conservative, so an unresolved "latest" pin
    /// never lets retention delete the package out from under a protected release.
    /// </summary>
    private static IEnumerable<(string PackageId, string Version)> ReferencedPackageVersions(
        Core.Domain.Releases.StepSnapshot step)
    {
        if (!step.Config.TryGetValue(KrakenScriptConfigKeys.PackageReferences, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        List<PackageReference>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<PackageReference>>(raw, RefJsonOpts);
        }
        catch (JsonException)
        {
            yield break;   // a malformed blob protects nothing (mirrors AgentPackageEntitlement)
        }

        if (parsed is null)
        {
            yield break;
        }

        foreach (var reference in parsed)
        {
            if (string.IsNullOrWhiteSpace(reference.PackageId))
            {
                continue;
            }
            yield return (reference.PackageId, reference.Version ?? "");
        }
    }

    private async Task<List<Guid>> ListSpaceIdsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Spaces
            .IgnoreQueryFilters()
            .Select(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the artifact-store root for the active account, mirroring
    /// <c>LocalArtifactStore.RootPath</c> (the store does not expose its root). The
    /// orphan sweep needs this to enumerate on-disk artifact directories.
    /// </summary>
    private string ResolveArtifactRoot()
    {
        var dataPath = configuration["Server:DataPath"] ?? "data";
        return accountContext.IsResolved
            ? Path.Combine(dataPath, "accounts", accountContext.CurrentAccountId.ToString(), "artifacts")
            : Path.Combine(dataPath, "artifacts");
    }

    /// <summary>Case-insensitive (packageId, version) equality — package ids match
    /// case-insensitively across the codebase (see <c>AgentPackageEntitlement</c>).</summary>
    private sealed class PackageVersionComparer : IEqualityComparer<(string, string)>
    {
        public static readonly PackageVersionComparer Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}

using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Creates deployments and enqueues them for dispatch to the target agent.
/// </summary>
public class DeploymentService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    Channel<Guid> deploymentQueue,
    TimeProvider time)
{
    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="Deployment"/> in the <c>Queued</c> state and hands it
    /// to the <see cref="DeploymentWorker"/> via the in-process channel.
    /// When <paramref name="scheduledFor"/> is a future timestamp the deployment
    /// is persisted but NOT dispatched — the Hangfire
    /// <c>ScheduledDeploymentDispatchJob</c> picks it up when the time arrives.
    /// Enforces the lifecycle gate if the release has a channel with a lifecycle.
    ///
    /// <para>
    /// M-RollingDeployments Phase 1b: <paramref name="additionalTargetIds"/>
    /// extends the deployment's target set beyond the legacy
    /// <paramref name="targetId"/>. When provided, the deployment dispatches
    /// against the union (primary + additional) — the orchestrator walks the
    /// <c>Deployment.Targets</c> join collection. The legacy
    /// <paramref name="targetId"/> stays the source of truth for code paths
    /// that haven't been upgraded yet (offline-drop, role-filter on server
    /// waves) and is also seeded into the join collection. Pass <c>null</c>
    /// or an empty list (the default) for single-target deployments —
    /// existing callers are unchanged.
    /// </para>
    /// </summary>
    public async Task<Deployment> CreateAsync(
        Guid releaseId,
        Guid environmentId,
        Guid targetId,
        Guid? tenantId = null,
        DateTimeOffset? scheduledFor = null,
        IReadOnlyCollection<Guid>? additionalTargetIds = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Validate release and environment exist.
        var releaseExists = await db.Releases.AnyAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false);
        if (!releaseExists)
        {
            throw new InvalidOperationException($"Release {releaseId} not found.");
        }

        var envExists = await db.Environments.AnyAsync(e => e.Id == environmentId, ct)
            .ConfigureAwait(false);
        if (!envExists)
        {
            throw new InvalidOperationException($"Environment {environmentId} not found.");
        }

        if (tenantId.HasValue)
        {
            var tenantExists = await db.Tenants.AnyAsync(t => t.Id == tenantId.Value, ct)
                .ConfigureAwait(false);
            if (!tenantExists)
            {
                throw new InvalidOperationException($"Tenant {tenantId.Value} not found.");
            }
        }

        // ── M-RollingDeployments Phase 1b — build the target id set ─────
        // Primary targetId is always part of the set (the legacy column +
        // first join row). Additional ids extend it; duplicates are
        // de-duplicated. Distinct against the primary so adding it twice
        // is a no-op.
        var targetIds = new List<Guid> { targetId };
        if (additionalTargetIds is not null)
        {
            foreach (var id in additionalTargetIds)
            {
                if (id != targetId && !targetIds.Contains(id))
                {
                    targetIds.Add(id);
                }
            }
        }
        if (targetIds.Count > 1)
        {
            // Validate every additional target exists in the same Space
            // BEFORE inserting the deployment so we don't leave a half-
            // created multi-target deployment if an id is bogus.
            var existing = await db.DeploymentTargets
                .Where(t => targetIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            var missing = targetIds.Where(id => !existing.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Target(s) not found: {string.Join(", ", missing)}.");
            }
        }

        // Enforce lifecycle phase gate (throws if gate not satisfied).
        await EnforceLifecycleGateAsync(db, releaseId, environmentId, tenantId, ct).ConfigureAwait(false);

        var deployment = new Deployment
        {
            ReleaseId = releaseId,
            EnvironmentId = environmentId,
            TargetId = targetId,
            TenantId = tenantId,
            Status = DeploymentStatus.Queued,
            ScheduledFor = scheduledFor,
        };

        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Seed the M-RollingDeployments join collection. The legacy
        // TargetId column is also kept in sync above for code paths that
        // haven't been upgraded yet.
        var now = time.GetUtcNow();
        foreach (var id in targetIds)
        {
            db.DeploymentTargetAssignments.Add(new DeploymentTargetAssignment
            {
                DeploymentId = deployment.Id,
                TargetId     = id,
                AddedUtc     = now,
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Dispatch immediately unless the caller requested a future start time.
        var isScheduledForFuture = scheduledFor.HasValue &&
            scheduledFor.Value > time.GetUtcNow();
        if (!isScheduledForFuture)
        {
            await deploymentQueue.Writer.WriteAsync(deployment.Id, ct).ConfigureAwait(false);
        }

        return deployment;
    }

    // ── Query ──────────────────────────────────────────────────────────────

    public async Task<List<Deployment>> GetAllAsync(
        Guid? projectId = null, int? limit = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var q = db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Target)
            .Include(d => d.Tenant)
            .AsQueryable();

        if (projectId.HasValue)
        {
            q = q.Where(d => d.Release.ProjectId == projectId.Value);
        }

        var ordered = q.OrderByDescending(d => d.CreatedUtc);
        // Cap the row count when a limit is given (e.g. the global Tasks page)
        // so an instance with a long history doesn't materialize every row.
        var bounded = limit is > 0 ? ordered.Take(limit.Value) : (IQueryable<Deployment>)ordered;
        return await bounded.ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Deployment?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // M-RollingDeployments Phase 3 — include the multi-target join so
        // the deployment-detail page can render the target set + map per-
        // outcome TargetIds to human-readable names without a second
        // round-trip.
        return await db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Target)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .Include(d => d.LogEntries.OrderBy(l => l.Sequence))
            .FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>
    /// M11.C — the AI diagnosis for a deployment, or null when none has been
    /// produced (AI disabled, diagnosis still running, or the deployment
    /// succeeded). Powers the "AI Analysis" card on the detail page.
    /// </summary>
    public async Task<KrakenDeploy.Server.Core.Domain.Ai.DeploymentDiagnosis?> GetDiagnosisAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentDiagnoses
            .FirstOrDefaultAsync(x => x.DeploymentId == deploymentId, ct);
    }

    /// <summary>
    /// Returns all output variables captured during a deployment via
    /// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> markers,
    /// ordered by step capture order and then variable name.
    /// </summary>
    public async Task<List<DeploymentOutputVariable>> GetOutputVariablesAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentOutputVariables
            .Where(o => o.DeploymentId == deploymentId)
            .OrderBy(o => o.CapturedUtc)
            .ThenBy(o => o.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// M14.5 — returns the terminal per-step outcomes captured during a
    /// deployment, ordered by <see cref="DeploymentStepOutcome.StepIndex"/>
    /// (== SortOrder rank in the process). Powers the deployment detail
    /// page's Steps tab.
    /// </summary>
    public async Task<List<DeploymentStepOutcome>> GetStepOutcomesAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentStepOutcomes
            .Where(o => o.DeploymentId == deploymentId)
            .OrderBy(o => o.StepIndex)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Builds a Tenant × Environment matrix of the latest deployment per cell
    /// for the given project. Returns every connected tenant and every space
    /// environment regardless of whether any deployment exists yet — empty
    /// cells are signalled by missing dictionary keys, not null values.
    /// </summary>
    public async Task<ProjectDashboardMatrix> GetProjectMatrixAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Tenants connected to this project (many-to-many via the Project.Tenants
        // navigation), ordered alphabetically for stable display.
        var tenants = await db.Projects
            .Where(p => p.Id == projectId)
            .SelectMany(p => p.Tenants)
            .OrderBy(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // All environments in the current Space (the global query filter scopes
        // this automatically through ISpaceScoped).
        var environments = await db.Environments
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Latest deployment per (tenantId, environmentId) for this project.
        // GroupBy + First-by-CreatedUtc would force client evaluation, so we
        // pull every deployment for the project (typically a small set) and
        // fold in memory.
        var rows = await db.Deployments
            .Where(d => d.Release.ProjectId == projectId && d.TenantId != null)
            .Include(d => d.Release).ThenInclude(r => r.Channel)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cells = new Dictionary<(Guid, Guid), DashboardCell>();
        foreach (var d in rows)
        {
            if (d.TenantId is null)
            {
                continue;
            }

            var key = (d.TenantId.Value, d.EnvironmentId);
            // First wins because the rows are ordered desc — that's the latest.
            if (cells.ContainsKey(key))
            {
                continue;
            }

            cells[key] = new DashboardCell(
                d.Id,
                d.Status,
                d.Release.Version,
                d.Release.Channel?.Name,
                d.CreatedUtc);
        }

        return new ProjectDashboardMatrix(tenants, environments, cells);
    }

    // ── Lifecycle gate ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether all earlier non-optional lifecycle phases have been satisfied
    /// for this release before allowing deployment to <paramref name="environmentId"/>.
    /// Silently succeeds if no lifecycle is configured.
    /// </summary>
    private static async Task EnforceLifecycleGateAsync(
        KrakenDbContext db, Guid releaseId, Guid environmentId, Guid? tenantId, CancellationToken ct)
    {
        // Load the lifecycle via: release → channel → lifecycle,
        // OR release → project → lifecycle (fallback).
        var release = await db.Releases
            .Include(r => r.Channel)
                .ThenInclude(c => c!.Lifecycle)
            .Include(r => r.Project)
                .ThenInclude(p => p.Lifecycle)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false);

        if (release is null)
        {
            return;
        }

        var lifecycle = release.Channel?.Lifecycle ?? release.Project.Lifecycle;
        if (lifecycle is null || lifecycle.Phases.Count == 0)
        {
            return;
        }

        var phases = lifecycle.Phases.OrderBy(p => p.SortOrder).ToList();

        // Find the index of the target environment's phase.
        var targetIdx = phases.FindIndex(p =>
            p.EnvironmentIds.Contains(environmentId) ||
            p.OptionalEnvironmentIds.Contains(environmentId));

        if (targetIdx <= 0)
        {
            return; // first phase, or environment not covered by lifecycle — allow
        }

        // Check all required phases before the target phase.
        for (var i = 0; i < targetIdx; i++)
        {
            var phase = phases[i];
            if (phase.IsOptional || phase.EnvironmentIds.Count == 0)
            {
                continue;
            }

            var minRequired = phase.MinimumEnvironments == 0
                ? phase.EnvironmentIds.Count
                : phase.MinimumEnvironments;

            // Count distinct environments in this phase that have a successful deployment.
            var envIds = phase.EnvironmentIds;
            var successQuery = db.Deployments
                .Where(d => d.ReleaseId == releaseId &&
                            envIds.Contains(d.EnvironmentId) &&
                            d.Status == DeploymentStatus.Succeeded);

            if (tenantId.HasValue)
            {
                successQuery = successQuery.Where(d => d.TenantId == tenantId.Value);
            }

            var successCount = await successQuery
                .Select(d => d.EnvironmentId)
                .Distinct()
                .CountAsync(ct)
                .ConfigureAwait(false);

            if (successCount < minRequired)
            {
                throw new InvalidOperationException(
                    $"Lifecycle gate: phase '{phase.Name}' requires successful deployment to " +
                    $"{minRequired} environment(s) but only {successCount} have succeeded for this release. " +
                    "Deploy to the required earlier environments first.");
            }
        }
    }
}

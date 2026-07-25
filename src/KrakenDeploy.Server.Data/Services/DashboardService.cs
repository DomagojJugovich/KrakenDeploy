using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read-only aggregation for the operator dashboard (<c>Home.razor</c>). All
/// queries run through the space-scoped <see cref="KrakenDbContext"/> global
/// filter, so counts and lists are already restricted to the active Space.
/// Stats are computed with COUNT queries (cheap on large histories); lists are
/// bounded. Nothing here mutates state.
/// </summary>
public sealed class DashboardService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ISpaceContext spaceContext)
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var dayAgo = now.AddHours(-24);
        var weekAgo = now.AddDays(-7);

        // ── Stat cards (COUNT only) ──────────────────────────────────────
        var deploymentsToday = await db.Deployments
            .CountAsync(d => (d.StartedUtc ?? d.CreatedUtc) >= todayStart, ct)
            .ConfigureAwait(false);

        var failed24h = await db.Deployments
            .CountAsync(d => d.Status == DeploymentStatus.Failed
                          && (d.CompletedUtc ?? d.CreatedUtc) >= dayAgo, ct)
            .ConfigureAwait(false);

        var pendingOffline = await db.Deployments
            .CountAsync(d => d.Status == DeploymentStatus.PendingOfflineResult, ct)
            .ConfigureAwait(false);

        // ── Targets (status only; cheap projection) ──────────────────────
        // Retired targets are excluded from the fleet summary — they're
        // decommissioned and would read as perpetually-offline, dragging down
        // the "X/Y online" count.
        var targetStatuses = await db.DeploymentTargets
            .AsNoTracking()
            .Where(t => !t.IsRetired)
            .Select(t => new { t.Id, t.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targetsTotal = targetStatuses.Count;
        var targetsOnline = targetStatuses.Count(t => t.Status == TargetStatus.Online);
        var onlineIds = targetStatuses
            .Where(t => t.Status == TargetStatus.Online)
            .Select(t => t.Id)
            .ToHashSet();

        // ── Recent + needs-attention lists ───────────────────────────────
        var recent = await WithNav(db)
            .OrderByDescending(d => d.CreatedUtc)
            .Take(8)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var attention = await WithNav(db)
            .Where(d => d.Status == DeploymentStatus.Failed
                     || d.Status == DeploymentStatus.PendingOfflineResult)
            .OrderByDescending(d => d.CreatedUtc)
            .Take(5)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ── Environment health ───────────────────────────────────────────
        var environments = await ComputeEnvironmentHealthAsync(db, onlineIds, ct).ConfigureAwait(false);

        var spaceName = await db.Spaces
            .Where(s => s.Id == spaceContext.CurrentSpaceId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? "Default";

        return new DashboardSummary(
            spaceName,
            targetsOnline, targetsTotal,
            deploymentsToday, failed24h, pendingOffline,
            recent, attention, environments);
    }

    /// <summary>
    /// Per-environment health (also used standalone by the Environments page).
    /// Targets aren't directly scoped to environments in the model (they carry
    /// roles, not an EnvironmentId), so "machines in this env" is derived
    /// honestly from deployment history: the distinct targets that have actually
    /// deployed to the env, and how many of those are online now.
    /// </summary>
    public async Task<IReadOnlyList<EnvironmentHealth>> GetEnvironmentHealthAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var onlineIds = (await db.DeploymentTargets
                .Where(t => t.Status == TargetStatus.Online)
                .Select(t => t.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        return await ComputeEnvironmentHealthAsync(db, onlineIds, ct).ConfigureAwait(false);
    }

    private static async Task<List<EnvironmentHealth>> ComputeEnvironmentHealthAsync(
        KrakenDbContext db, HashSet<Guid> onlineIds, CancellationToken ct)
    {
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        var envs = await db.Environments
            .AsNoTracking()
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var weekByEnv = (await db.Deployments
                .Where(d => d.CreatedUtc >= weekAgo)
                .GroupBy(d => d.EnvironmentId)
                .Select(g => new { EnvId = g.Key, Count = g.Count() })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(x => x.EnvId, x => x.Count);

        // Env health is deployment-centric; the unified assignment table now also
        // holds runbook-run targets, so restrict to deployment-kind tasks to keep
        // "machines that deployed here" honest.
        var envTargetPairs = await db.TaskTargetAssignments
            .Where(a => a.Task.Kind == ServerTaskKind.Deployment)
            .Select(a => new { a.Task.EnvironmentId, a.TargetId })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targetsByEnv = envTargetPairs
            .GroupBy(p => p.EnvironmentId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.TargetId).ToList());

        return envs.Select(e =>
        {
            var tids = targetsByEnv.GetValueOrDefault(e.Id) ?? [];
            return new EnvironmentHealth(
                e.Id,
                e.Name,
                weekByEnv.GetValueOrDefault(e.Id),
                TargetsOnline: tids.Count(id => onlineIds.Contains(id)),
                TargetsTotal: tids.Count);
        }).ToList();
    }

    /// <summary>
    /// Release × environment matrix for a project's Overview page: rows are the
    /// newest releases, cells the latest deployment of that release into each
    /// environment. Read-only; bounded to the newest <paramref name="maxReleases"/>.
    /// </summary>
    public async Task<ReleaseMatrix> GetReleaseMatrixAsync(
        Guid projectId, int maxReleases = 25, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var environments = await db.Environments
            .AsNoTracking()
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var releases = await db.Releases
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .Include(r => r.Channel)
            .OrderByDescending(r => r.CreatedUtc)
            .Take(maxReleases)
            .Select(r => new { r.Id, r.Version, r.CreatedUtc, ChannelName = r.Channel != null ? r.Channel.Name : null })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var releaseIds = releases.Select(r => r.Id).ToList();

        var deployments = await db.Deployments
            .AsNoTracking()
            .Where(d => releaseIds.Contains(d.ReleaseId))
            .Select(d => new { d.Id, d.ReleaseId, d.EnvironmentId, d.Status, d.CreatedUtc, d.StartedUtc, d.CompletedUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestCells = deployments
            .GroupBy(d => (d.ReleaseId, d.EnvironmentId))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(d => d.CreatedUtc).First();
                    return new ReleaseMatrixCell(
                        latest.Id,
                        latest.Status,
                        latest.CompletedUtc ?? latest.StartedUtc ?? latest.CreatedUtc);
                });

        var rows = releases.Select(r => new ReleaseMatrixRow(
                r.Id, r.Version, r.CreatedUtc, r.ChannelName,
                environments
                    .Where(e => latestCells.ContainsKey((r.Id, e.Id)))
                    .ToDictionary(e => e.Id, e => latestCells[(r.Id, e.Id)])))
            .ToList();

        return new ReleaseMatrix(rows, environments);
    }

    /// <summary>
    /// Project × environment dashboard (the Projects landing page): one row per
    /// project, grouped by Project Group, with the latest deployment into each
    /// environment (release version, status, when) plus a tenant-coverage badge
    /// — how many of the project's connected tenants are on that current release
    /// in that environment. Read-only; mirrors Octopus's project dashboard.
    /// </summary>
    public async Task<ProjectDashboard> GetProjectDashboardAsync(
        ProjectDashboardFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= ProjectDashboardFilter.All;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var envQuery = db.Environments.AsNoTracking().AsQueryable();
        if (!filter.AllEnvironments)
        {
            envQuery = envQuery.Where(e => filter.EnvironmentIds.Contains(e.Id));
        }
        var environments = await envQuery
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var projectQuery = db.Projects.AsNoTracking().AsQueryable();
        if (!filter.AllGroups)
        {
            projectQuery = projectQuery.Where(p => filter.GroupIds.Contains(p.ProjectGroupId));
        }
        if (!filter.AllProjects)
        {
            projectQuery = projectQuery.Where(p => filter.ProjectIds.Contains(p.Id));
        }

        var projects = await projectQuery
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Slug,
                GroupId = p.ProjectGroupId,
                GroupName = p.ProjectGroup != null ? p.ProjectGroup.Name : null,
                GroupSort = p.ProjectGroup != null ? p.ProjectGroup.SortOrder : int.MaxValue,
                // Denominator of the tenant-coverage badge honours the tenant
                // filter: when only some tenants are shown, count only those.
                TenantsConnected = filter.AllTenants
                    ? p.Tenants.Count
                    : p.Tenants.Count(t => filter.TenantIds.Contains(t.Id)),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Minimal projection of the whole deployment history (same scoping the
        // env-health query already does over all deployments). Latest-per-cell
        // and tenant coverage are computed in memory below.
        var depQuery = db.Deployments.AsNoTracking().AsQueryable();
        if (!filter.AllTenants)
        {
            depQuery = depQuery.Where(d => d.TenantId != null && filter.TenantIds.Contains(d.TenantId.Value));
        }
        var deps = await depQuery
            .Select(d => new
            {
                d.Id,
                ProjectId = d.ProjectId,
                d.EnvironmentId,
                d.ReleaseId,
                Version = d.Release.Version,
                Channel = d.Release.Channel != null ? d.Release.Channel.Name : null,
                d.TenantId,
                d.Status,
                d.CreatedUtc,
                d.StartedUtc,
                d.CompletedUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var cellsByProject = deps
            .GroupBy(d => d.ProjectId)
            .ToDictionary(
                pg => pg.Key,
                pg => pg
                    .GroupBy(d => d.EnvironmentId)
                    .ToDictionary(
                        eg => eg.Key,
                        eg =>
                        {
                            var latest = eg.OrderByDescending(d => d.CreatedUtc).First();
                            var onRelease = eg
                                .Where(d => d.ReleaseId == latest.ReleaseId && d.TenantId != null)
                                .Select(d => d.TenantId!.Value)
                                .Distinct()
                                .Count();
                            return new ProjectDashboardCell(
                                latest.Id,
                                latest.Version,
                                latest.Channel,
                                latest.Status,
                                latest.CompletedUtc ?? latest.StartedUtc ?? latest.CreatedUtc,
                                onRelease);
                        }));

        var rowsByGroup = projects
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.GroupId, Row: new ProjectDashboardRow(
                p.Id, p.Name, p.Slug, p.TenantsConnected,
                cellsByProject.GetValueOrDefault(p.Id) ?? [])))
            .ToLookup(x => x.GroupId, x => x.Row);

        // Sections come from the groups TABLE (not just groups that happen to
        // have projects), so a freshly created group shows up empty.
        var groupQuery = db.ProjectGroups.AsNoTracking().AsQueryable();
        if (!filter.AllGroups)
        {
            groupQuery = groupQuery.Where(g => filter.GroupIds.Contains(g.Id));
        }
        var allGroups = await groupQuery
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
            .Select(g => new { g.Id, g.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var groups = allGroups
            .Select(g => new ProjectGroupSection(g.Id, g.Name, rowsByGroup[g.Id].ToList()))
            .ToList();
        // Every project belongs to a group (project_group_id is NOT NULL), so
        // there is no "Ungrouped" bucket to emit.

        return new ProjectDashboard(groups, environments);
    }

    /// <summary>
    /// Flat per-deployment fact rows for the dashboard analytics pivot —
    /// star-schema grain: one row per deployment, dimensions denormalized to
    /// display strings, measures summable (Count / IsFailure / DurationSeconds)
    /// so any client-side re-aggregation stays correct. Bounded by
    /// <paramref name="fromUtc"/> and <paramref name="maxRows"/> (newest first).
    /// </summary>
    public async Task<IReadOnlyList<DeploymentFact>> GetDeploymentFactsAsync(
        DateTimeOffset fromUtc, int maxRows = 10_000, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await db.Deployments
            .AsNoTracking()
            .Where(d => d.CreatedUtc >= fromUtc)
            .OrderByDescending(d => d.CreatedUtc)
            .Take(maxRows)
            .Select(d => new
            {
                d.Id,
                Project = d.Release.Project.Name,
                Tenant = d.Tenant != null ? d.Tenant.Name : null,
                Environment = d.Environment.Name,
                Release = d.Release.Version,
                Channel = d.Release.Channel != null ? d.Release.Channel.Name : null,
                // Pivot facts are one row per deployment; attribute it to the
                // first-assigned (canonical) target, matching what the old
                // single-target column recorded.
                Target = d.Targets
                    .OrderBy(a => a.AddedUtc).ThenBy(a => a.TargetId)
                    .Select(a => a.Target!.Name)
                    .FirstOrDefault(),
                d.Status,
                d.CreatedUtc,
                d.StartedUtc,
                d.CompletedUtc,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(d =>
        {
            var when = (d.StartedUtc ?? d.CreatedUtc).ToLocalTime();
            var duration = d.CompletedUtc is { } end && d.StartedUtc is { } start
                ? (end - start).TotalSeconds
                : 0d;

            return new DeploymentFact(
                d.Id,
                d.Project,
                d.Tenant ?? "—",
                d.Environment,
                d.Release,
                d.Channel ?? "Default",
                d.Target ?? "—",
                d.Status.ToString(),
                when,
                Day: when.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Week: string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{System.Globalization.ISOWeek.GetYear(when.DateTime)}-W{System.Globalization.ISOWeek.GetWeekOfYear(when.DateTime):00}"),
                Month: when.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                DurationSeconds: duration,
                IsFailure: d.Status == DeploymentStatus.Failed ? 1 : 0,
                IsSuccess: d.Status is DeploymentStatus.Succeeded or DeploymentStatus.SucceededWithWarnings ? 1 : 0);
        }).ToList();
    }

    private static IQueryable<Deployment> WithNav(KrakenDbContext db) =>
        db.Deployments
            .AsNoTracking()
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target);
}

/// <summary>One deployment, flattened for pivoting (dimensions + summable measures).</summary>
public sealed record DeploymentFact(
    Guid DeploymentId,
    string Project,
    string Tenant,
    string Environment,
    string Release,
    string Channel,
    string Target,
    string Status,
    DateTimeOffset When,
    string Day,
    string Week,
    string Month,
    double DurationSeconds,
    int IsFailure,
    int IsSuccess);

/// <summary>
/// Per-user filter for the Projects dashboard: show all, or only a selected
/// subset of project groups / projects / environments / tenants. Serialized to
/// <c>ProjectDashboardView.Definition</c>; the empty-list + All=true shape is
/// the unfiltered default.
/// </summary>
public sealed record ProjectDashboardFilter(
    bool AllGroups, IReadOnlyList<Guid> GroupIds,
    bool AllProjects, IReadOnlyList<Guid> ProjectIds,
    bool AllEnvironments, IReadOnlyList<Guid> EnvironmentIds,
    bool AllTenants, IReadOnlyList<Guid> TenantIds)
{
    public static ProjectDashboardFilter All { get; } =
        new(true, [], true, [], true, [], true, []);

    /// <summary>True when any axis is restricted to a subset.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => !AllGroups || !AllProjects || !AllEnvironments || !AllTenants;
}

/// <summary>Project × environment dashboard (Projects landing page), grouped by Project Group.</summary>
public sealed record ProjectDashboard(
    IReadOnlyList<ProjectGroupSection> Groups,
    IReadOnlyList<DeploymentEnvironment> Environments);

public sealed record ProjectGroupSection(
    Guid? GroupId,
    string GroupName,
    IReadOnlyList<ProjectDashboardRow> Projects);

public sealed record ProjectDashboardRow(
    Guid ProjectId,
    string Name,
    string Slug,
    int TenantsConnected,
    IReadOnlyDictionary<Guid, ProjectDashboardCell> Cells);

/// <summary>Latest deployment of a project into one environment, with tenant coverage.</summary>
public sealed record ProjectDashboardCell(
    Guid DeploymentId,
    string ReleaseVersion,
    string? ChannelName,
    DeploymentStatus Status,
    DateTimeOffset When,
    int TenantsOnRelease);

/// <summary>Release × environment matrix for a project Overview page.</summary>
public sealed record ReleaseMatrix(
    IReadOnlyList<ReleaseMatrixRow> Releases,
    IReadOnlyList<DeploymentEnvironment> Environments);

public sealed record ReleaseMatrixRow(
    Guid ReleaseId,
    string Version,
    DateTimeOffset CreatedUtc,
    string? ChannelName,
    IReadOnlyDictionary<Guid, ReleaseMatrixCell> Cells);

/// <summary>Latest deployment of a release into one environment.</summary>
public sealed record ReleaseMatrixCell(
    Guid DeploymentId,
    DeploymentStatus Status,
    DateTimeOffset When);

/// <summary>Aggregated, read-only snapshot powering the operator dashboard.</summary>
public sealed record DashboardSummary(
    string SpaceName,
    int TargetsOnline,
    int TargetsTotal,
    int DeploymentsToday,
    int Failed24h,
    int PendingOffline,
    IReadOnlyList<Deployment> Recent,
    IReadOnlyList<Deployment> Attention,
    IReadOnlyList<EnvironmentHealth> Environments);

/// <summary>Per-environment health row. Target counts reflect distinct targets
/// that have deployed to the environment (see <see cref="DashboardService"/>).</summary>
public sealed record EnvironmentHealth(
    Guid EnvironmentId,
    string Name,
    int DeploysThisWeek,
    int TargetsOnline,
    int TargetsTotal);

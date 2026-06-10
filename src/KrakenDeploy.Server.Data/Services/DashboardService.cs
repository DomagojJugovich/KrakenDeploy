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
        var targetStatuses = await db.DeploymentTargets
            .AsNoTracking()
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

        var envTargetPairs = await db.Deployments
            .Where(d => d.TargetId != null)
            .Select(d => new { d.EnvironmentId, TargetId = d.TargetId!.Value })
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

    private static IQueryable<Deployment> WithNav(KrakenDbContext db) =>
        db.Deployments
            .AsNoTracking()
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Target);
}

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

using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Server-wide aggregates for the license banner and the License Usage
/// dashboard. All counts are computed under <c>IgnoreQueryFilters</c> because
/// license caps span every Space, not just the ambient one.
///
/// <para>
/// The cap-bearing pair (<c>(Targets, Users)</c>) is hot-cached with a 60 s
/// TTL — the banner renders on every navigation and we don't want one COUNT
/// per page-view per user. The richer dashboard projections (per-Space
/// rollup, "other resources") are NOT cached: they're only queried by the
/// usage page, which is administrator-only and visited rarely.
/// </para>
/// </summary>
public sealed class LicenseUsageCounter(
    IServiceScopeFactory scopeFactory,
    TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private (int Targets, int Users)? _snapshot;
    private DateTimeOffset _refreshedAt;

    /// <summary>
    /// Returns the cached <c>(targetCount, userCount)</c> snapshot, refreshing
    /// it from the DB if older than 60 s. Counts are server-wide.
    /// </summary>
    public async Task<(int Targets, int Users)> GetSnapshotAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_snapshot is { } cached && (now - _refreshedAt) < CacheTtl)
            {
                return cached;
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<Data.Identity.ApplicationUser>>();
        var targets = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .CountAsync(ct)
            .ConfigureAwait(false);
        var users = await userManager.Users.CountAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            _snapshot = (targets, users);
            _refreshedAt = time.GetUtcNow();
            return _snapshot.Value;
        }
    }

    /// <summary>
    /// Forces the next <see cref="GetSnapshotAsync"/> to hit the DB. Call
    /// after a known-mutating write path (e.g. successful target registration)
    /// if you want the banner to update immediately rather than waiting up
    /// to 60 s.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    /// <summary>
    /// Returns a per-Space target count rollup, joined to Space names so the
    /// dashboard can show "Production: 7, Staging: 2, ...". Spaces with zero
    /// targets are included — operators want to see them as "0/—" instead
    /// of disappearing from the table.
    /// </summary>
    public async Task<IReadOnlyList<SpaceTargetCount>> GetPerSpaceTargetCountsAsync(
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        // LEFT JOIN style — every Space appears even with zero targets.
        var rollup = await db.Spaces
            .IgnoreQueryFilters()
            .OrderBy(s => s.Name)
            .Select(s => new SpaceTargetCount(
                s.Id,
                s.Name,
                db.DeploymentTargets
                    .IgnoreQueryFilters()
                    .Count(t => t.SpaceId == s.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rollup;
    }

    /// <summary>
    /// Counts for resources that are NOT capped by the license today but are
    /// useful capacity-planning context next to the gauges (Projects, Tenants,
    /// Environments, Spaces). When the license model later adds caps for any
    /// of these, the dashboard can promote them to gauges without a schema
    /// change here.
    /// </summary>
    public async Task<OtherResourceCounts> GetOtherResourceCountsAsync(
        CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();

        // Each of these is ISpaceScoped except Spaces itself — IgnoreQueryFilters
        // ensures the count is server-wide for parity with the cap reporting.
        var projects     = await db.Projects.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var tenants      = await db.Tenants.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var environments = await db.Environments.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);
        var spaces       = await db.Spaces.IgnoreQueryFilters().CountAsync(ct).ConfigureAwait(false);

        return new OtherResourceCounts(projects, tenants, environments, spaces);
    }
}

/// <summary>One row in the per-Space target rollup.</summary>
public sealed record SpaceTargetCount(Guid SpaceId, string SpaceName, int TargetCount);

/// <summary>Counts for resources that the license doesn't cap (yet).</summary>
public sealed record OtherResourceCounts(
    int Projects, int Tenants, int Environments, int Spaces);

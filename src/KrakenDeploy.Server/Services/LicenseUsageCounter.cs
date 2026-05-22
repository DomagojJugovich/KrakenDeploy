using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Cheap-and-cached snapshot of the server-wide counts the license cares
/// about (targets + users). Used by the banner so we don't requery the DB
/// on every Blazor re-render — the banner sits in MainLayout and renders on
/// every navigation. A 60-second TTL is good enough: limits are advisory in
/// the UI (the hard stop is enforced inside the data services), and an
/// operator adding their tenth target won't suffer for a one-minute lag
/// before the banner switches from "approaching" to "reached".
/// </summary>
public sealed class LicenseUsageCounter(
    IDbContextFactory<KrakenDbContext> dbFactory,
    UserManager<Data.Identity.ApplicationUser> userManager,
    TimeProvider time)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private (int Targets, int Users)? _snapshot;
    private DateTimeOffset _refreshedAt;

    /// <summary>
    /// Returns the cached <c>(targetCount, userCount)</c> snapshot, refreshing
    /// it from the DB if older than 60 s. Counts are server-wide
    /// (<c>IgnoreQueryFilters</c> for targets) because license caps span
    /// every Space, not just the ambient one.
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

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
}

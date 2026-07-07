using KrakenDeploy.Server.Core.Domain.Dashboards;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Per-user saved dashboard tile arrangements. Private to the owner — every
/// operation is scoped by user id (and the ambient Space via the global query
/// filter). At most one row per user per dashboard key.
/// </summary>
public sealed class DashboardLayoutService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<DashboardLayout?> GetForUserAsync(
        Guid userId, string dashboardKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardKey);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DashboardLayouts
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.UserId == userId && l.DashboardKey == dashboardKey, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Creates or overwrites the user's saved layout for a dashboard.</summary>
    public async Task UpsertAsync(
        Guid userId, string dashboardKey, string definition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.DashboardLayouts
            .FirstOrDefaultAsync(l => l.UserId == userId && l.DashboardKey == dashboardKey, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Definition = definition;
        }
        else
        {
            db.DashboardLayouts.Add(new DashboardLayout
            {
                UserId = userId,
                DashboardKey = dashboardKey,
                Definition = definition,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the user's saved layout for a dashboard (reset to default).</summary>
    public async Task ClearAsync(Guid userId, string dashboardKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardKey);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.DashboardLayouts
            .FirstOrDefaultAsync(l => l.UserId == userId && l.DashboardKey == dashboardKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            db.DashboardLayouts.Remove(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

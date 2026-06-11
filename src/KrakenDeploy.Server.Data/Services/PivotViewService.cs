using KrakenDeploy.Server.Core.Domain.Analytics;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Per-user saved pivot layouts for the dashboard analytics table. Views are
/// private to their owner — every operation is scoped by user id, so a stolen
/// view id can't read or delete someone else's view.
/// </summary>
public sealed class PivotViewService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<List<PivotView>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.PivotViews
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .OrderBy(v => v.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Creates or overwrites the user's view with the given name.</summary>
    public async Task<PivotView> UpsertAsync(
        Guid userId, string name, string definition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.PivotViews
            .FirstOrDefaultAsync(v => v.UserId == userId && v.Name == name, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Definition = definition;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existing;
        }

        var view = new PivotView { UserId = userId, Name = name.Trim(), Definition = definition };
        db.PivotViews.Add(view);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return view;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid viewId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var view = await db.PivotViews
            .FirstOrDefaultAsync(v => v.Id == viewId && v.UserId == userId, ct)
            .ConfigureAwait(false);

        if (view is null)
        {
            return false;
        }

        db.PivotViews.Remove(view);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}

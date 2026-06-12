using KrakenDeploy.Server.Core.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Per-user saved default filter for the Projects dashboard. Private to its
/// owner — every operation is scoped by user id. Stores at most one row per
/// user (their default view).
/// </summary>
public sealed class ProjectDashboardViewService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<ProjectDashboardView?> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ProjectDashboardViews
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.UserId == userId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Creates or overwrites the user's saved default filter.</summary>
    public async Task UpsertAsync(Guid userId, string definition, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.ProjectDashboardViews
            .FirstOrDefaultAsync(v => v.UserId == userId, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Definition = definition;
        }
        else
        {
            db.ProjectDashboardViews.Add(new ProjectDashboardView { UserId = userId, Definition = definition });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.ProjectDashboardViews
            .FirstOrDefaultAsync(v => v.UserId == userId, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            db.ProjectDashboardViews.Remove(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

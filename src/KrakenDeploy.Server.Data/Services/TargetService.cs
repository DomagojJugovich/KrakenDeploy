using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class TargetService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    public async Task<List<DeploymentTarget>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentTargets.OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Distinct roles across every deployment target, sorted. Roles live in a
    /// jsonb list so flattening happens in memory — target counts are small.
    /// </summary>
    public async Task<List<string>> GetAllRolesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roleLists = await db.DeploymentTargets
            .AsNoTracking()
            .Select(t => t.Roles)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return roleLists
            .SelectMany(r => r)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<DeploymentTarget?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DeploymentTargets.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(DeploymentTarget target, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.DeploymentTargets.Update(target);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

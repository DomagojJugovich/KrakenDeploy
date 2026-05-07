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

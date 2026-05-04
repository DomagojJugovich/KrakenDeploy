using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class TargetService(KrakenDbContext db)
{
    public Task<List<DeploymentTarget>> GetAllAsync(CancellationToken ct = default)
        => db.DeploymentTargets.OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<DeploymentTarget?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.DeploymentTargets.FindAsync([id], ct).ConfigureAwait(false);

    public async Task UpdateAsync(DeploymentTarget target, CancellationToken ct = default)
    {
        db.DeploymentTargets.Update(target);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

public class TargetService(KrakenDbContext db)
{
    public Task<List<DeploymentTarget>> GetAllAsync(CancellationToken ct = default)
        => db.DeploymentTargets.OrderBy(t => t.Name).ToListAsync(ct);
}

using System.Threading.Channels;
using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Creates deployments and enqueues them for dispatch to the target agent.
/// </summary>
public class DeploymentService(
    KrakenDbContext db,
    Channel<Guid> deploymentQueue)
{
    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="Deployment"/> in the <c>Queued</c> state and hands it
    /// to the <see cref="DeploymentWorker"/> via the in-process channel.
    /// </summary>
    public async Task<Deployment> CreateAsync(
        Guid releaseId,
        Guid environmentId,
        Guid targetId,
        CancellationToken ct = default)
    {
        // Validate release and environment exist.
        var releaseExists = await db.Releases.AnyAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false);
        if (!releaseExists)
        {
            throw new InvalidOperationException($"Release {releaseId} not found.");
        }

        var envExists = await db.Environments.AnyAsync(e => e.Id == environmentId, ct)
            .ConfigureAwait(false);
        if (!envExists)
        {
            throw new InvalidOperationException($"Environment {environmentId} not found.");
        }

        var deployment = new Deployment
        {
            ReleaseId = releaseId,
            EnvironmentId = environmentId,
            TargetId = targetId,
            Status = DeploymentStatus.Queued,
        };

        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Enqueue for async dispatch — fire and forget inside the process.
        await deploymentQueue.Writer.WriteAsync(deployment.Id, ct).ConfigureAwait(false);

        return deployment;
    }

    // ── Query ──────────────────────────────────────────────────────────────

    public async Task<List<Deployment>> GetAllAsync(
        Guid? projectId = null, CancellationToken ct = default)
    {
        var q = db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Target)
            .AsQueryable();

        if (projectId.HasValue)
        {
            q = q.Where(d => d.Release.ProjectId == projectId.Value);
        }

        return await q.OrderByDescending(d => d.CreatedUtc).ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<Deployment?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Target)
            .Include(d => d.LogEntries.OrderBy(l => l.Sequence))
            .FirstOrDefaultAsync(d => d.Id == id, ct);
}

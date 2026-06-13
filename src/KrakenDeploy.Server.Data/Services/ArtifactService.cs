using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.ArtifactStorage;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Saves, lists, and retrieves deployment artifacts.
/// </summary>
public sealed class ArtifactService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IArtifactStore store)
{
    // ── Write ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists <paramref name="content"/> to the artifact store and writes a
    /// <see cref="DeploymentArtifact"/> record to the database.
    /// </summary>
    public async Task<DeploymentArtifact> SaveAsync(
        Guid deploymentId,
        string stepName,
        string fileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken ct = default)
    {
        var storedPath = await store.SaveAsync(deploymentId, stepName, fileName, content, ct)
            .ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Agent-upload path has no real Space context — resolve the parent
        // deployment's Space directly (IgnoreQueryFilters) and stamp it so the
        // interceptor doesn't mis-assign the Default Space.
        var spaceId = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.Id == deploymentId)
            .Select(d => d.SpaceId)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        var artifact = new DeploymentArtifact
        {
            SpaceId      = spaceId,
            DeploymentId = deploymentId,
            StepName     = stepName,
            FileName     = fileName,
            ContentType  = contentType,
            SizeBytes    = sizeBytes,
            StoredPath   = storedPath,
            CollectedUtc = DateTimeOffset.UtcNow,
        };

        db.DeploymentArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return artifact;
    }

    // ── Query ──────────────────────────────────────────────────────────────────

    // DeploymentArtifact isn't ISpaceScoped — it reaches a Space via its parent
    // Deployment. Read paths therefore scope transitively through the
    // (space-filtered) Deployments set, so an artifact/deployment GUID from
    // another Space can't be read or downloaded across the request-path API.
    public async Task<List<DeploymentArtifact>> GetByDeploymentAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentArtifacts
             .Where(a => a.DeploymentId == deploymentId
                      && db.Deployments.Any(d => d.Id == a.DeploymentId))
             .OrderBy(a => a.StepName).ThenBy(a => a.FileName)
             .ToListAsync(ct);
    }

    public async Task<DeploymentArtifact?> GetAsync(Guid artifactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DeploymentArtifacts
            .FirstOrDefaultAsync(a => a.Id == artifactId
                                   && db.Deployments.Any(d => d.Id == a.DeploymentId), ct);
    }

    // ── Download ───────────────────────────────────────────────────────────────

    public async Task<(Stream Stream, DeploymentArtifact Artifact)> OpenReadAsync(
        Guid artifactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var artifact = await db.DeploymentArtifacts
            .FirstOrDefaultAsync(a => a.Id == artifactId
                                   && db.Deployments.Any(d => d.Id == a.DeploymentId), ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Artifact {artifactId} not found.");

        var stream = await store.OpenReadAsync(artifact.StoredPath, ct).ConfigureAwait(false);
        return (stream, artifact);
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(Guid artifactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var artifact = await db.DeploymentArtifacts
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct).ConfigureAwait(false);
        if (artifact is null)
        {
            return;
        }

        store.Delete(artifact.StoredPath);
        db.DeploymentArtifacts.Remove(artifact);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

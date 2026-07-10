using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.ArtifactStorage;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Saves, lists, and retrieves task artifacts (deployment or runbook run).
/// </summary>
public sealed class ArtifactService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IArtifactStore store)
{
    // ── Write ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persists <paramref name="content"/> to the artifact store and writes a
    /// <see cref="TaskArtifact"/> record to the database.
    /// </summary>
    public async Task<TaskArtifact> SaveAsync(
        Guid taskId,
        string stepName,
        string fileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken ct = default)
    {
        var storedPath = await store.SaveAsync(taskId, stepName, fileName, content, ct)
            .ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Agent-upload path has no real Space context — resolve the parent task's
        // Space directly (IgnoreQueryFilters) and stamp it so the interceptor
        // doesn't mis-assign the Default Space.
        var spaceId = await db.ServerTasks.IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .Select(t => t.SpaceId)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        var artifact = new TaskArtifact
        {
            SpaceId      = spaceId,
            TaskId       = taskId,
            StepName     = stepName,
            FileName     = fileName,
            ContentType  = contentType,
            SizeBytes    = sizeBytes,
            StoredPath   = storedPath,
            CollectedUtc = DateTimeOffset.UtcNow,
        };

        db.TaskArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return artifact;
    }

    // ── Query ──────────────────────────────────────────────────────────────────

    // TaskArtifact is ISpaceScoped, so the global query filter scopes these reads
    // to the caller's Space; a task/artifact GUID from another Space simply won't
    // match.
    public async Task<List<TaskArtifact>> GetByTaskAsync(
        Guid taskId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TaskArtifacts
             .Where(a => a.TaskId == taskId)
             .OrderBy(a => a.StepName).ThenBy(a => a.FileName)
             .ToListAsync(ct);
    }

    public async Task<TaskArtifact?> GetAsync(Guid artifactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TaskArtifacts.FirstOrDefaultAsync(a => a.Id == artifactId, ct);
    }

    // ── Download ───────────────────────────────────────────────────────────────

    public async Task<(Stream Stream, TaskArtifact Artifact)> OpenReadAsync(
        Guid artifactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var artifact = await db.TaskArtifacts
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Artifact {artifactId} not found.");

        var stream = await store.OpenReadAsync(artifact.StoredPath, ct).ConfigureAwait(false);
        return (stream, artifact);
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(Guid artifactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var artifact = await db.TaskArtifacts
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct).ConfigureAwait(false);
        if (artifact is null)
        {
            return;
        }

        store.Delete(artifact.StoredPath);
        db.TaskArtifacts.Remove(artifact);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

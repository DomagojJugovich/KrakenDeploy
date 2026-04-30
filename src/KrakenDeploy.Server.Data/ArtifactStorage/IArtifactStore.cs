namespace KrakenDeploy.Server.Data.ArtifactStorage;

/// <summary>
/// Abstracts the physical storage of deployment artifact files.
/// The default implementation writes to the local filesystem; the interface
/// exists so a future implementation can swap in S3, Azure Blob, etc.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Saves <paramref name="content"/> and returns the store-relative path
    /// that should be persisted in <c>DeploymentArtifact.StoredPath</c>.
    /// </summary>
    Task<string> SaveAsync(
        Guid deploymentId,
        string stepName,
        string fileName,
        Stream content,
        CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for the artifact at <paramref name="storedPath"/>
    /// (as previously returned by <see cref="SaveAsync"/>).
    /// </summary>
    Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default);

    /// <summary>Deletes the artifact file.  Non-fatal if it doesn't exist.</summary>
    void Delete(string storedPath);
}

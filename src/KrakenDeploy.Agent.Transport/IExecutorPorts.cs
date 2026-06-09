namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Port the <c>DeploymentExecutor</c> uses to obtain a step's package zip,
/// independent of transport. Online this is <see cref="GrpcPackageDownloader"/>
/// (gRPC + delta + cache); offline it's a bundle-backed implementation that
/// copies the package out of the drop bundle's <c>packages/</c> directory.
/// <para>
/// Deliberately free of server URL / agent-token parameters — those are an
/// online concern resolved by the gRPC implementation itself (via accessor
/// delegates), so the same executor code runs unchanged offline.
/// </para>
/// </summary>
public interface IPackageSource
{
    /// <summary>
    /// Materialises <paramref name="packageId"/> v<paramref name="version"/>
    /// into <paramref name="destDirectory"/> and returns the full path of the
    /// resulting zip file.
    /// </summary>
    Task<string> DownloadAsync(
        string packageId, string version, string destDirectory, CancellationToken ct);
}

/// <summary>
/// Port the <c>DeploymentExecutor</c> uses to persist a step artifact,
/// independent of transport. Online this is <see cref="GrpcArtifactUploader"/>
/// (streams to the server); offline it's a bundle-backed implementation that
/// copies the file into the drop bundle's <c>artifacts/</c> directory.
/// </summary>
public interface IArtifactSink
{
    /// <summary>
    /// Persists <paramref name="filePath"/> as an artifact of
    /// <paramref name="deploymentId"/> / <paramref name="stepName"/>. Returns
    /// an identifier on success or <see langword="null"/> on failure (failure
    /// is logged; the caller continues with other artifacts).
    /// </summary>
    Task<string?> UploadAsync(
        Guid deploymentId, string stepName, string filePath, CancellationToken ct);
}

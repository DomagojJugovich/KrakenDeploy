using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// A file collected from the agent after a deployment step completes.
/// <para>
/// Agents write files to the per-step <c>KRAKEN_ARTIFACTS_PATH</c> directory
/// (implicitly) or via <c>Register-KrakenArtifact</c> (explicitly).
/// The executor streams each file to the server using the gRPC
/// <c>ArtifactUpload.Upload</c> RPC after the step finishes.
/// </para>
/// </summary>
public sealed class DeploymentArtifact : Entity, ISpaceScoped
{
    /// <summary>Inherited from the parent Deployment; set explicitly in
    /// ArtifactService.SaveAsync (agent upload path has no real Space context).</summary>
    public Guid   SpaceId { get; set; }

    public Guid   DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Name of the deployment step that produced this artifact.</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>Original file name as it appeared on the agent (basename only).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME content type; defaults to <c>application/octet-stream</c>.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Size of the stored file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Path of the file on the server's artifact store, relative to the store root.
    /// Consumers should obtain a stream via <c>IArtifactStore.OpenReadAsync</c>
    /// rather than reading this path directly.
    /// </summary>
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the artifact was received by the server.</summary>
    public DateTimeOffset CollectedUtc { get; set; }
}

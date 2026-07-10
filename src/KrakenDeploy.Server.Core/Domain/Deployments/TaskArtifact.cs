using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// A file collected from the agent after a task step completes (formerly
/// <c>DeploymentArtifact</c>). Agents write files to the per-step
/// <c>KRAKEN_ARTIFACTS_PATH</c> directory (implicitly) or via
/// <c>Register-KrakenArtifact</c> (explicitly); the executor streams each file to
/// the server over the gRPC <c>ArtifactUpload.Upload</c> RPC after the step finishes.
/// </summary>
public sealed class TaskArtifact : Entity, ISpaceScoped
{
    /// <summary>Inherited from the parent task; set explicitly in
    /// ArtifactService.SaveAsync (agent upload path has no real Space context).</summary>
    public Guid   SpaceId { get; set; }

    public Guid   TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>Name of the task step that produced this artifact.</summary>
    public string StepName { get; set; } = string.Empty;

    /// <summary>Original file name as it appeared on the agent (basename only).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME content type; defaults to <c>application/octet-stream</c>.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Size of the stored file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Path of the file on the server's artifact store, relative to the
    /// store root. Obtain a stream via <c>IArtifactStore.OpenReadAsync</c>.</summary>
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the artifact was received by the server.</summary>
    public DateTimeOffset CollectedUtc { get; set; }
}

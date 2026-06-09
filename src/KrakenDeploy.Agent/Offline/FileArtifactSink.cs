using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts.Offline;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Offline;

/// <summary>
/// Offline <see cref="IArtifactSink"/>: persists step artifacts into the
/// bundle's <c>artifacts/{step}/</c> directory instead of streaming them to the
/// server. The returned id is the bundle-relative path; the operator ships the
/// artifacts back with the result.
/// </summary>
public sealed class FileArtifactSink(string bundleRoot, ILogger<FileArtifactSink>? logger = null)
    : IArtifactSink
{
    public Task<string?> UploadAsync(
        Guid deploymentId, string stepName, string filePath, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(
                bundleRoot, OfflineBundleLayout.ArtifactsDir,
                OfflineBundleLayout.SanitizeStepName(stepName));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(filePath));
            File.Copy(filePath, dest, overwrite: true);
            var rel = Path.GetRelativePath(bundleRoot, dest).Replace('\\', '/');
            return Task.FromResult<string?>(rel);
        }
        catch (Exception ex)
        {
            // IArtifactSink contract: log + return null on failure so the
            // executor continues with other artifacts and an artifact IO error
            // never fails an otherwise-successful step (mirrors GrpcArtifactUploader).
            logger?.LogWarning(ex,
                "Failed to persist artifact '{File}' for step '{Step}'.", filePath, stepName);
            return Task.FromResult<string?>(null);
        }
    }
}

using System.Security.Claims;
using Grpc.Core;
using KrakenDeploy.Contracts.Grpc;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.ArtifactStorage;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// gRPC service that receives artifact files uploaded by an agent after each
/// deployment step.
/// <para>
/// The agent opens a client-streaming call and sends chunks.  The first chunk
/// must include the metadata fields (<c>deployment_id</c>, <c>step_name</c>,
/// <c>file_name</c>, <c>content_type</c>); subsequent chunks only need <c>data</c>.
/// The server assembles the stream and persists it via <see cref="IArtifactStore"/>.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class GrpcArtifactUploadService(
    ArtifactService artifactService,
    IDbContextFactory<KrakenDbContext> dbFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<GrpcArtifactUploadService> logger)
    : ArtifactUpload.ArtifactUploadBase
{
    private const int ChunkSize = 64 * 1024; // 64 KB read buffer

    public override async Task<ArtifactUploadResult> Upload(
        IAsyncStreamReader<ArtifactChunk> requestStream,
        ServerCallContext context)
    {
        try
        {
            // Read header from first chunk.
            if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                return Fail("Empty upload stream.");
            }

            var first = requestStream.Current;

            if (string.IsNullOrWhiteSpace(first.DeploymentId) ||
                string.IsNullOrWhiteSpace(first.StepName)     ||
                string.IsNullOrWhiteSpace(first.FileName))
            {
                return Fail("First chunk must contain deployment_id, step_name, and file_name.");
            }

            if (!Guid.TryParse(first.DeploymentId, out var deploymentId))
            {
                return Fail($"Invalid deployment_id: '{first.DeploymentId}'.");
            }

            var stepName    = first.StepName;
            var fileName    = Path.GetFileName(first.FileName); // basename only
            var contentType = string.IsNullOrWhiteSpace(first.ContentType)
                              ? ContentTypeFromFileName(fileName)
                              : first.ContentType;

            logger.LogInformation(
                "Receiving artifact '{FileName}' for deployment {DeploymentId} step '{StepName}'.",
                fileName, deploymentId, stepName);

            // Ownership: the uploading agent may only attach artifacts to a
            // deployment its OWN authenticated target participates in — the same
            // trust boundary as the AgentHub log/complete path. Checked BEFORE
            // buffering the stream so an unauthorized agent can't push bytes.
            var rawTarget = httpContextAccessor.HttpContext?.User
                .FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(rawTarget, out var connectionTargetId))
            {
                return Fail("Unauthenticated artifact upload.");
            }

            await using (var db = await dbFactory
                .CreateDbContextAsync(context.CancellationToken).ConfigureAwait(false))
            {
                if (!await AgentDeploymentOwnership.ConnectionOwnsDeploymentAsync(
                        db, deploymentId, connectionTargetId).ConfigureAwait(false))
                {
                    logger.LogWarning(
                        "Artifact upload rejected: target {Target} is not assigned to deployment {Id}.",
                        connectionTargetId, deploymentId);
                    return Fail("Not authorized for this deployment.");
                }
            }

            // Pipe all chunks through a PipeStream into ArtifactService.
            using var ms = new MemoryStream();

            // Write first chunk's data.
            if (first.Data is { IsEmpty: false })
            {
                await ms.WriteAsync(first.Data.Memory, context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (!first.IsLast)
            {
                await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken)
                    .ConfigureAwait(false))
                {
                    if (chunk.Data is { IsEmpty: false })
                    {
                        await ms.WriteAsync(chunk.Data.Memory, context.CancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (chunk.IsLast) { break; }
                }
            }

            ms.Position = 0;
            var sizeBytes = ms.Length;

            var artifact = await artifactService.SaveAsync(
                deploymentId, stepName, fileName, contentType, sizeBytes, ms,
                context.CancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Artifact '{FileName}' ({Bytes:N0} bytes) saved for deployment {DeploymentId}.",
                fileName, sizeBytes, deploymentId);

            return new ArtifactUploadResult
            {
                ArtifactId = artifact.Id.ToString(),
                Success    = true,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to receive artifact upload.");
            return Fail(ex.Message);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ArtifactUploadResult Fail(string error) =>
        new() { Success = false, Error = error };

    private static string ContentTypeFromFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            ".svg"  => "image/svg+xml",
            ".html" => "text/html",
            ".htm"  => "text/html",
            ".txt"  => "text/plain",
            ".log"  => "text/plain",
            ".xml"  => "application/xml",
            ".json" => "application/json",
            ".pdf"  => "application/pdf",
            ".zip"  => "application/zip",
            _       => "application/octet-stream",
        };
}

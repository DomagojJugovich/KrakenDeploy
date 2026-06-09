using System.Net.Http.Headers;
using Grpc.Net.Client;
using KrakenDeploy.Contracts.Grpc;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Uploads artifact files to the server's gRPC <c>ArtifactUpload</c> service
/// after each deployment step completes.
/// <para>
/// Files are streamed in 64 KB chunks.  The first chunk carries the metadata
/// header (deployment id, step name, file name, content type); subsequent
/// chunks carry raw bytes.
/// </para>
/// </summary>
public sealed class GrpcArtifactUploader(
    Func<string> serverUrlAccessor,
    Func<string> agentTokenAccessor,
    ILogger<GrpcArtifactUploader> logger) : IArtifactSink, IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024; // 64 KB

    // Channel is reused across calls for the same server URL.
    private string?      _channelServerUrl;
    private GrpcChannel? _channel;
    private ArtifactUpload.ArtifactUploadClient? _client;

    /// <summary>
    /// Uploads a single artifact file.  Returns the server-assigned artifact id
    /// on success, or <see langword="null"/> on failure (error is logged; caller
    /// should continue uploading other artifacts).
    /// </summary>
    public async Task<string?> UploadAsync(
        Guid deploymentId,
        string stepName,
        string filePath,
        CancellationToken ct)
    {
        var serverUrl   = serverUrlAccessor();
        var agentToken  = agentTokenAccessor();
        var fileName    = Path.GetFileName(filePath);
        var contentType = ContentTypeFromFileName(fileName);

        logger.LogInformation(
            "Uploading artifact '{FileName}' for deployment {DeploymentId} step '{StepName}'.",
            fileName, deploymentId, stepName);

        try
        {
            var client = GetOrCreateClient(serverUrl, agentToken);
            var call   = client.Upload(cancellationToken: ct);

            await using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: ChunkSize, useAsync: true);

            var buffer   = new byte[ChunkSize];
            var isFirst  = true;
            int bytesRead;

            while ((bytesRead = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                var isLast = fs.Position == fs.Length;

                var chunk = new ArtifactChunk
                {
                    Data   = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                    IsLast = isLast,
                };

                if (isFirst)
                {
                    chunk.DeploymentId = deploymentId.ToString();
                    chunk.StepName     = stepName;
                    chunk.FileName     = fileName;
                    chunk.ContentType  = contentType;
                    isFirst            = false;
                }

                await call.RequestStream.WriteAsync(chunk, ct).ConfigureAwait(false);

                if (isLast) { break; }
            }

            // Empty file edge-case — still send the header so the server records it.
            if (isFirst)
            {
                await call.RequestStream.WriteAsync(new ArtifactChunk
                {
                    DeploymentId = deploymentId.ToString(),
                    StepName     = stepName,
                    FileName     = fileName,
                    ContentType  = contentType,
                    IsLast       = true,
                }, ct).ConfigureAwait(false);
            }

            await call.RequestStream.CompleteAsync().ConfigureAwait(false);

            var result = await call.ResponseAsync.ConfigureAwait(false);
            if (!result.Success)
            {
                logger.LogWarning(
                    "Server rejected artifact '{FileName}': {Error}", fileName, result.Error);
                return null;
            }

            logger.LogInformation(
                "Artifact '{FileName}' uploaded successfully (id={ArtifactId}).",
                fileName, result.ArtifactId);
            return result.ArtifactId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to upload artifact '{FileName}' for deployment {DeploymentId}.",
                fileName, deploymentId);
            return null;
        }
    }

    // ── Channel management ─────────────────────────────────────────────────────

    private ArtifactUpload.ArtifactUploadClient GetOrCreateClient(
        string serverUrl, string agentToken)
    {
        if (_client is not null && _channelServerUrl == serverUrl)
        {
            return _client;
        }

        _channel?.Dispose();
        _channel = null;
        _client  = null;

        AppContext.SetSwitch(
            "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);

        _channel           = GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions { HttpClient = httpClient });
        _client            = new ArtifactUpload.ArtifactUploadClient(_channel);
        _channelServerUrl  = serverUrl;
        return _client;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.ShutdownAsync().ConfigureAwait(false);
            _channel.Dispose();
        }
    }
}

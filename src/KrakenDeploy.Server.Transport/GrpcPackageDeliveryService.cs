using Grpc.Core;
using KrakenDeploy.Contracts.Grpc;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// gRPC service that streams a package file to the requesting agent.
/// Authenticated with the same "AgentJwt" bearer scheme as <see cref="AgentHub"/>.
/// </summary>
[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class GrpcPackageDeliveryService(
    KrakenDbContext db,
    IPackageStore packageStore,
    ILogger<GrpcPackageDeliveryService> logger)
    : PackageDelivery.PackageDeliveryBase
{
    private const int ChunkSize = 64 * 1024; // 64 KB chunks

    public override async Task Download(
        DownloadRequest request,
        IServerStreamWriter<DownloadChunk> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var package = await db.Packages
            .FirstOrDefaultAsync(
                p => p.PackageId == request.PackageId && p.Version == request.Version,
                context.CancellationToken)
            .ConfigureAwait(false)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Package '{request.PackageId}' version '{request.Version}' not found."));

        logger.LogInformation(
            "Streaming package {PackageId} v{Version} ({Bytes:N0} bytes) to agent.",
            package.PackageId, package.Version, package.SizeBytes);

        await using var fileStream = await packageStore
            .OpenReadAsync(package.StoredPath, context.CancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        var isFirst = true;
        int bytesRead;

        while ((bytesRead = await fileStream
                   .ReadAsync(buffer, context.CancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            var chunk = new DownloadChunk
            {
                Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                TotalBytes = isFirst ? package.SizeBytes : 0,
                IsLast = false,
            };

            await responseStream.WriteAsync(chunk, context.CancellationToken)
                .ConfigureAwait(false);

            isFirst = false;
        }

        // Send an explicit last-chunk marker so the agent can finalise without
        // relying solely on stream-end detection.
        await responseStream.WriteAsync(
            new DownloadChunk { Data = Google.Protobuf.ByteString.Empty, IsLast = true },
            context.CancellationToken).ConfigureAwait(false);
    }
}

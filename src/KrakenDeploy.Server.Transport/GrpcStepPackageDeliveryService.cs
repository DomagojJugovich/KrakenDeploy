using System.Security.Claims;
using System.Security.Cryptography;
using Grpc.Core;
using KrakenDeploy.Contracts.Grpc;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// gRPC service that streams a <c>.kdeploy-step</c> archive to the
/// requesting agent (Phase D-5). Authenticated with the same agent JWT
/// scheme as <see cref="AgentHub"/> and <see cref="GrpcPackageDeliveryService"/>.
/// <para>
/// Pulls the original signed zip directly from
/// <c>{dataPath}/step-packages/{name}/{version}/package.kdeploy-step</c> —
/// the bytes <see cref="KrakenDeploy.Server.Data.Services.StepPackageService"/>
/// persisted alongside the extracted form. Computes the SHA-256 of the
/// transferred bytes on the fly and sends it on the trailer chunk so the
/// agent can verify against tampering in transit.
/// </para>
/// <para>
/// No delta transfer / no resume here — packages are small (typically a
/// few MB) and a full re-fetch on failure is simpler than the resumption
/// bookkeeping that <see cref="GrpcPackageDeliveryService"/> carries for
/// big deployment payloads.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class GrpcStepPackageDeliveryService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IConfiguration config,
    IHttpContextAccessor httpContextAccessor,
    ILogger<GrpcStepPackageDeliveryService> logger)
    : StepPackageDelivery.StepPackageDeliveryBase
{
    private const int ChunkSize = 64 * 1024; // 64 KB — same as PackageDelivery.

    private Guid? AgentTargetId()
    {
        var raw = httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public override async Task DownloadStepPackage(
        StepPackageDownloadRequest request,
        IServerStreamWriter<StepPackageChunk> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ct = context.CancellationToken;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ── Entitlement ───────────────────────────────────────────────────────
        // An agent may only download a step package some deployment dispatched to
        // ITS target references (by StepPackageName). Denied before the row lookup.
        var targetId = AgentTargetId();
        if (targetId is null
            || !await AgentPackageEntitlement.TargetMayDownloadStepPackageAsync(
                db, targetId.Value, request.Name, ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Step-package download denied: target {Target} is not entitled to step package {Name}.",
                targetId, request.Name);
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "Calling agent is not entitled to this step package."));
        }

        // ── Resolve the install row ───────────────────────────────────────────
        var row = await db.StepPackages
            .FirstOrDefaultAsync(p => p.Name == request.Name && p.Version == request.Version, ct)
            .ConfigureAwait(false)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"Step package '{request.Name}' version '{request.Version}' is not installed."));

        // ── Resolve the archive path ──────────────────────────────────────────
        var root        = config["DataPath"] ?? "data";
        var archivePath = Path.Combine(root, "step-packages",
            SanitisePathSegment(row.Name), SanitisePathSegment(row.Version),
            "package" + StepPackageFiles.Extension);

        if (!File.Exists(archivePath))
        {
            logger.LogError(
                "Step package {Name} {Version} install row exists but archive file is missing at {Path}.",
                row.Name, row.Version, archivePath);
            throw new RpcException(new Status(StatusCode.Internal,
                "Step package install is missing its archive file on disk."));
        }

        // ── Stream chunks, hashing on the fly ─────────────────────────────────
        await using var fs    = File.OpenRead(archivePath);
        var totalBytes        = fs.Length;
        var buffer            = new byte[ChunkSize];
        using var sha         = SHA256.Create();
        var bytesSent         = 0L;
        var isFirstChunk      = true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false);
            if (read == 0)
            {
                // EOF — emit the trailer chunk with the SHA-256.
                sha.TransformFinalBlock([], 0, 0);
                var digest = Convert.ToHexStringLower(sha.Hash!);

                await responseStream.WriteAsync(new StepPackageChunk
                {
                    Data       = Google.Protobuf.ByteString.Empty,
                    TotalBytes = 0,
                    IsLast     = true,
                    Sha256     = digest,
                }, ct).ConfigureAwait(false);

                logger.LogDebug(
                    "Streamed step package {Name} {Version} ({Bytes} bytes, sha256={Sha}).",
                    row.Name, row.Version, bytesSent, digest);
                return;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            bytesSent += read;

            await responseStream.WriteAsync(new StepPackageChunk
            {
                Data       = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
                TotalBytes = isFirstChunk ? totalBytes : 0,
                IsLast     = false,
                Sha256     = "",
            }, ct).ConfigureAwait(false);
            isFirstChunk = false;
        }
    }

    /// <summary>
    /// Mirrors the same defensive path-segment sanitisation
    /// <see cref="KrakenDeploy.Server.Data.Services.StepPackageService"/>
    /// uses on upload; keeping the logic identical means the lookup here
    /// always resolves to whatever the upload wrote.
    /// </summary>
    private static string SanitisePathSegment(string s)
        => string.Join('_',
            s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
         .Replace("..", "_", StringComparison.Ordinal);
}

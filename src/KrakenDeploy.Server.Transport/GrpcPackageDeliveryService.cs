using System.Security.Claims;
using System.Security.Cryptography;
using Grpc.Core;
using KrakenDeploy.Contracts.Grpc;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// gRPC service that streams a package file to the requesting agent.
/// Authenticated with the same "AgentJwt" bearer scheme as <see cref="AgentHub"/>.
/// <para>
/// Supports two transfer modes:
/// <list type="bullet">
///   <item><b>Delta</b> — when the agent supplies a <c>base_version</c> it already
///   has in cache, the server streams an Octodiff delta instead of the full zip.
///   Falls back to a full download if delta generation fails or the base is missing.</item>
///   <item><b>Full / resumable</b> — optional <c>resume_offset</c> causes the server
///   to seek into the file and stream from that byte position onward.</item>
/// </list>
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = "AgentJwt")]
public sealed class GrpcPackageDeliveryService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPackageStore packageStore,
    PackageDeltaService deltaService,
    IHttpContextAccessor httpContextAccessor,
    ILogger<GrpcPackageDeliveryService> logger)
    : PackageDelivery.PackageDeliveryBase
{
    private const int ChunkSize = 64 * 1024; // 64 KB

    private Guid? AgentTargetId()
    {
        var raw = httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public override async Task Download(
        DownloadRequest request,
        IServerStreamWriter<DownloadChunk> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ct = context.CancellationToken;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ── Entitlement ───────────────────────────────────────────────────────
        // An agent may only download a package some deployment dispatched to ITS
        // target references (primary or referenced). Closes cross-package
        // exfiltration; checked on PackageId so the delta base version (same id,
        // resolved below) is covered by the same decision. Denied before the row
        // lookup so an unentitled caller gets no package-existence oracle.
        var targetId = AgentTargetId();
        if (targetId is null
            || !await AgentPackageEntitlement.TargetMayDownloadPackageAsync(
                db, targetId.Value, request.PackageId, ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Package download denied: target {Target} is not entitled to package {PackageId}.",
                targetId, request.PackageId);
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "Calling agent is not entitled to this package."));
        }

        // ── Resolve the requested package ─────────────────────────────────────
        var package = await db.Packages
            .FirstOrDefaultAsync(
                p => p.PackageId == request.PackageId && p.Version == request.Version, ct)
            .ConfigureAwait(false)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Package '{request.PackageId}' version '{request.Version}' not found."));

        // ── Route to delta or full path ───────────────────────────────────────
        if (!string.IsNullOrEmpty(request.BaseVersion))
        {
            await ServeDeltaAsync(db, request, package, responseStream, ct).ConfigureAwait(false);
        }
        else
        {
            await ServeFullAsync(request, package, responseStream, ct).ConfigureAwait(false);
        }
    }

    // ── Delta path ────────────────────────────────────────────────────────────

    private async Task ServeDeltaAsync(
        KrakenDbContext db,
        DownloadRequest request,
        Package package,
        IServerStreamWriter<DownloadChunk> responseStream,
        CancellationToken ct)
    {
        var basePackage = await db.Packages
            .FirstOrDefaultAsync(
                p => p.PackageId == request.PackageId && p.Version == request.BaseVersion, ct)
            .ConfigureAwait(false);

        if (basePackage is null)
        {
            logger.LogWarning(
                "Delta requested but base {PackageId} v{Base} not found; serving full file.",
                request.PackageId, request.BaseVersion);
            await ServeFullAsync(request, package, responseStream, ct).ConfigureAwait(false);
            return;
        }

        var basePath = packageStore.GetFullPath(basePackage.StoredPath);
        var newPath  = packageStore.GetFullPath(package.StoredPath);

        MemoryStream? delta;
        try
        {
            delta = await deltaService.BuildDeltaAsync(basePath, newPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Delta generation failed for {PackageId} {Base}→{New}; serving full file.",
                request.PackageId, request.BaseVersion, request.Version);
            delta = null;
        }

        if (delta is null)
        {
            await ServeFullAsync(request, package, responseStream, ct).ConfigureAwait(false);
            return;
        }

        logger.LogInformation(
            "Streaming delta for {PackageId} v{Base}→v{New} ({DeltaBytes:N0} bytes).",
            request.PackageId, request.BaseVersion, request.Version, delta.Length);

        await using (delta)
        {
            // No trailer hash for deltas: Octodiff's DeltaApplier verifies the
            // reconstructed zip end-to-end on the agent (SkipHashCheck = false).
            await StreamFromAsync(
                delta, isDelta: true, totalBytes: delta.Length, emitHash: false,
                responseStream, ct)
                .ConfigureAwait(false);
        }
    }

    // ── Full / resumable path ─────────────────────────────────────────────────

    private async Task ServeFullAsync(
        DownloadRequest request,
        Package package,
        IServerStreamWriter<DownloadChunk> responseStream,
        CancellationToken ct)
    {
        var resumeOffset = request.ResumeOffset;
        var reportedSize = resumeOffset > 0
            ? package.SizeBytes - resumeOffset
            : package.SizeBytes;

        logger.LogInformation(
            "Streaming package {PackageId} v{Version} ({Bytes:N0} bytes){Resume}.",
            package.PackageId, package.Version, package.SizeBytes,
            resumeOffset > 0 ? $" from offset {resumeOffset:N0}" : string.Empty);

        await using var fileStream = await packageStore
            .OpenReadAsync(package.StoredPath, ct).ConfigureAwait(false);

        if (resumeOffset > 0 && fileStream.CanSeek)
        {
            fileStream.Seek(resumeOffset, SeekOrigin.Begin);
        }

        // Only emit the integrity hash when this transfer covers the WHOLE file.
        // A resumed transfer streams a partial range, so its on-the-fly hash
        // wouldn't match the full zip — suppress it then (the current agent never
        // resumes; verification still applies to the normal full path).
        await StreamFromAsync(
            fileStream, isDelta: false, totalBytes: reportedSize,
            emitHash: resumeOffset == 0, responseStream, ct)
            .ConfigureAwait(false);
    }

    // ── Shared streaming loop ─────────────────────────────────────────────────

    private static async Task StreamFromAsync(
        Stream source,
        bool isDelta,
        long totalBytes,
        bool emitHash,
        IServerStreamWriter<DownloadChunk> responseStream,
        CancellationToken ct)
    {
        var buffer  = new byte[ChunkSize];
        var isFirst = true;
        int bytesRead;

        using var sha = emitHash ? SHA256.Create() : null;

        while ((bytesRead = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha?.TransformBlock(buffer, 0, bytesRead, null, 0);

            var chunk = new DownloadChunk
            {
                Data       = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                TotalBytes = isFirst ? totalBytes : 0,
                IsLast     = false,
                IsDelta    = isDelta,
            };

            await responseStream.WriteAsync(chunk, ct).ConfigureAwait(false);
            isFirst = false;
        }

        var digest = string.Empty;
        if (sha is not null)
        {
            sha.TransformFinalBlock([], 0, 0);
            digest = Convert.ToHexStringLower(sha.Hash!);
        }

        // Explicit end-of-stream marker so the agent does not depend solely on
        // gRPC stream completion for finalisation. Carries the full-zip SHA-256
        // on a non-delta, non-resumed transfer.
        await responseStream.WriteAsync(
            new DownloadChunk
            {
                Data    = Google.Protobuf.ByteString.Empty,
                IsLast  = true,
                IsDelta = isDelta,
                Sha256  = digest,
            }, ct).ConfigureAwait(false);
    }
}

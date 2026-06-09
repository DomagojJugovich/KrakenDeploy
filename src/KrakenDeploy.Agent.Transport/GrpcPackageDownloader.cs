using System.Net.Http.Headers;
using Grpc.Core;
using Grpc.Net.Client;
using KrakenDeploy.Contracts.Grpc;
using Microsoft.Extensions.Logging;
using Octodiff.Core;
using Octodiff.Diagnostics;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Downloads package files from the server's gRPC <c>PackageDelivery</c> service.
/// <para>
/// Transfer strategy (in priority order):
/// <list type="number">
///   <item>Cache hit — the requested version is already on disk; file is copied
///   to the staging directory with no network round-trip.</item>
///   <item>Delta — the agent has a different version of the same package in cache;
///   the server is asked for an Octodiff delta.  The agent applies it to the cached
///   file to produce the target version.</item>
///   <item>Full download — no usable cache entry; the full zip is streamed.
///   Supports a <c>resume_offset</c> to restart an interrupted transfer.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GrpcPackageDownloader(
    IPackageCache cache,
    Func<string> serverUrlAccessor,
    Func<string> agentTokenAccessor,
    ILogger<GrpcPackageDownloader> logger) : IPackageSource, IAsyncDisposable
{
    // Lazily created and cached; recreated if the server URL changes.
    private string? _channelServerUrl;
    private GrpcChannel? _channel;
    private PackageDelivery.PackageDeliveryClient? _client;

    /// <summary>
    /// Downloads (or retrieves from cache) the specified package, places it in
    /// <paramref name="destDirectory"/>, and returns the full path of the zip file.
    /// </summary>
    public async Task<string> DownloadAsync(
        string packageId,
        string version,
        string destDirectory,
        CancellationToken ct)
    {
        var serverUrl  = serverUrlAccessor();
        var agentToken = agentTokenAccessor();

        // ── 1. Cache hit ──────────────────────────────────────────────────────
        var cachedPath = cache.TryGetCachedPath(packageId, version);
        if (cachedPath is not null)
        {
            logger.LogInformation(
                "Package {PackageId} v{Version} found in local cache; skipping download.",
                packageId, version);
            return CopyToStaging(cachedPath, destDirectory, packageId, version);
        }

        // ── 2. Pick delta base (most-recent cached version of this package) ───
        var cachedVersions = cache.GetCachedVersions(packageId);
        var baseVersion    = cachedVersions.Count > 0 ? cachedVersions[^1] : null;

        // ── 3. Request download from server ───────────────────────────────────
        var client  = GetOrCreateClient(serverUrl, agentToken);
        var request = new DownloadRequest
        {
            PackageId   = packageId,
            Version     = version,
            BaseVersion = baseVersion ?? string.Empty,
        };

        logger.LogInformation(
            "Downloading package {PackageId} v{Version} via gRPC{Delta}…",
            packageId, version,
            baseVersion is not null ? $" (requesting delta from v{baseVersion})" : string.Empty);

        var call = client.Download(request, cancellationToken: ct);

        Directory.CreateDirectory(destDirectory);
        var tempPath = Path.Combine(destDirectory, $"{packageId}.{version}.tmp");

        long totalBytesReceived = 0;
        bool isDelta            = false;

        await using (var tempFile = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true))
        {
            await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (chunk.IsLast)
                {
                    break;
                }

                isDelta = chunk.IsDelta;
                var data = chunk.Data.Memory;
                await tempFile.WriteAsync(data, ct).ConfigureAwait(false);
                totalBytesReceived += data.Length;

                if (chunk.TotalBytes > 0)
                {
                    logger.LogDebug(
                        "Transfer started; {TotalBytes:N0} bytes expected ({Mode}).",
                        chunk.TotalBytes, isDelta ? "delta" : "full");
                }
            }
        }

        logger.LogInformation(
            "Received {Bytes:N0} bytes ({Mode}).",
            totalBytesReceived, isDelta ? "delta" : "full");

        // ── 4. Apply delta or rename to final zip ─────────────────────────────
        string finalPath;

        if (isDelta && baseVersion is not null)
        {
            var baseCachePath = cache.TryGetCachedPath(packageId, baseVersion)!;
            finalPath         = Path.Combine(destDirectory, $"{packageId}.{version}.zip");

            logger.LogInformation(
                "Applying Octodiff delta: {PackageId} v{Base}→v{New}…",
                packageId, baseVersion, version);

            await ApplyDeltaAsync(baseCachePath, tempPath, finalPath, ct).ConfigureAwait(false);

            File.Delete(tempPath);
        }
        else
        {
            finalPath = Path.ChangeExtension(tempPath, ".zip");
            File.Move(tempPath, finalPath, overwrite: true);
        }

        // ── 5. Cache the resulting zip ────────────────────────────────────────
        await cache.StoreAsync(packageId, version, finalPath, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Package {PackageId} v{Version} cached and staged at {Path}.",
            packageId, version, finalPath);

        return finalPath;
    }

    // ── Delta apply ───────────────────────────────────────────────────────────

    private static Task ApplyDeltaAsync(
        string basePath, string deltaPath, string outputPath, CancellationToken ct)
        => Task.Run(() =>
        {
            using var basis  = new FileStream(basePath,  FileMode.Open,   FileAccess.Read,      FileShare.Read);
            using var delta  = new FileStream(deltaPath, FileMode.Open,   FileAccess.Read,      FileShare.Read);
            // ReadWrite is required: DeltaApplier seeks back to position 0 and reads the
            // output stream to compute the end-to-end hash when SkipHashCheck = false.
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            new DeltaApplier { SkipHashCheck = false }
                .Apply(basis, new BinaryDeltaReader(delta, new NullProgressReporter()), output);
        }, ct);

    // ── Staging helper ────────────────────────────────────────────────────────

    private static string CopyToStaging(
        string cachedPath, string destDir, string packageId, string version)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, $"{packageId}.{version}.zip");
        File.Copy(cachedPath, dest, overwrite: true);
        return dest;
    }

    // ── Channel management ────────────────────────────────────────────────────

    private PackageDelivery.PackageDeliveryClient GetOrCreateClient(
        string serverUrl, string agentToken)
    {
        if (_client is not null && _channelServerUrl == serverUrl)
        {
            return _client;
        }

        // Dispose old channel if the server URL changed.
        _channel?.Dispose();
        _channel = null;
        _client  = null;

        // Allow HTTP/2 over plain text for development / smoke-test environments.
        AppContext.SetSwitch(
            "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);

        _channel = GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        _client          = new PackageDelivery.PackageDeliveryClient(_channel);
        _channelServerUrl = serverUrl;
        return _client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.ShutdownAsync().ConfigureAwait(false);
            _channel.Dispose();
        }
    }
}

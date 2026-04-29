using System.Net.Http.Headers;
using Grpc.Core;
using Grpc.Net.Client;
using KrakenDeploy.Contracts.Grpc;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Downloads package files from the server's gRPC <c>PackageDelivery</c> service.
/// The JWT token and server URL are supplied per-call so this class has no dependency
/// on the identity subsystem.
/// </summary>
public sealed class GrpcPackageDownloader(ILogger<GrpcPackageDownloader> logger) : IAsyncDisposable
{
    // Lazily created and cached; keyed by server URL so a server change recreates the channel.
    private string? _channelServerUrl;
    private GrpcChannel? _channel;
    private PackageDelivery.PackageDeliveryClient? _client;

    /// <summary>
    /// Downloads the package to <paramref name="destDirectory"/> and returns
    /// the full path of the downloaded file.
    /// </summary>
    public async Task<string> DownloadAsync(
        string serverUrl,
        string agentToken,
        string packageId,
        string version,
        string destDirectory,
        CancellationToken ct)
    {
        var client = GetOrCreateClient(serverUrl, agentToken);
        var fileName = $"{packageId}.{version}.zip";
        var filePath = Path.Combine(destDirectory, fileName);

        logger.LogInformation(
            "Downloading package {PackageId} v{Version} via gRPC…", packageId, version);

        var call = client.Download(
            new DownloadRequest { PackageId = packageId, Version = version },
            cancellationToken: ct);

        Directory.CreateDirectory(destDirectory);
        await using var fileStream = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        long totalBytes = 0;
        await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (chunk.IsLast)
            {
                break;
            }

            var data = chunk.Data.Memory;
            await fileStream.WriteAsync(data, ct).ConfigureAwait(false);
            totalBytes += data.Length;

            if (chunk.TotalBytes > 0)
            {
                logger.LogDebug(
                    "Package download started; total {TotalBytes:N0} bytes.", chunk.TotalBytes);
            }
        }

        logger.LogInformation(
            "Package {PackageId} v{Version} downloaded ({Bytes:N0} bytes) → {Path}.",
            packageId, version, totalBytes, filePath);

        return filePath;
    }

    // ── Channel management ─────────────────────────────────────────────────

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
        _client = null;

        // Allow HTTP/2 over plain text for development/smoke-test environments.
        AppContext.SetSwitch(
            "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);

        _channel = GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        _client = new PackageDelivery.PackageDeliveryClient(_channel);
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

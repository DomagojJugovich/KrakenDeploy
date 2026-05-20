using System.Net.Http.Headers;
using System.Security.Cryptography;
using Grpc.Core;
using Grpc.Net.Client;
using KrakenDeploy.Contracts.Grpc;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Streams a <c>.kdeploy-step</c> archive from the server's
/// <c>StepPackageDelivery</c> gRPC service (Phase D-5) and hands the assembled
/// archive to a configured extractor (typically
/// <c>StepPackageLoader.ExtractToCache</c>).
/// <para>
/// No delta / no resume — packages are small (typically a few MB) and a
/// clean re-fetch on failure is simpler than the resumption bookkeeping the
/// big-payload <see cref="GrpcPackageDownloader"/> carries.
/// </para>
/// <para>
/// SHA-256 verification: the server sends the digest in the trailer chunk
/// (<c>is_last = true</c>). The downloader hashes the chunk bytes incrementally
/// while writing them to a temp file and compares the two digests before
/// handing the archive to the extractor — a tampered byte anywhere on the
/// wire fails the check loudly.
/// </para>
/// </summary>
public sealed class GrpcStepPackageDownloader : IStepPackageSource, IAsyncDisposable
{
    private readonly Func<string> _serverUrl;
    private readonly Func<string> _agentToken;
    private readonly Func<string, string, string, Task> _extract;
    private readonly ILogger<GrpcStepPackageDownloader> _logger;

    private string? _channelServerUrl;
    private GrpcChannel? _channel;
    private StepPackageDelivery.StepPackageDeliveryClient? _client;

    /// <summary>
    /// Constructs the downloader.
    /// </summary>
    /// <param name="serverUrl">
    /// Resolves the server base URL at call time (e.g. closes over <c>AgentContext.Identity!.ServerUrl</c>).
    /// Resolved lazily so the downloader can be registered as a singleton before
    /// registration has completed.
    /// </param>
    /// <param name="agentToken">Resolves the bearer JWT presented to the gRPC service. Same accessor pattern as <paramref name="serverUrl"/>.</param>
    /// <param name="extract">
    /// Callback the downloader invokes once the archive has been streamed and
    /// verified. Signature: <c>(name, version, archivePath) -> Task</c>. The
    /// implementation is expected to extract the archive into the loader's
    /// cache directory. Wired in production to <c>StepPackageLoader.ExtractToCache</c>;
    /// tests pass a stub.
    /// </param>
    /// <param name="logger">Logger.</param>
    public GrpcStepPackageDownloader(
        Func<string> serverUrl,
        Func<string> agentToken,
        Func<string, string, string, Task> extract,
        ILogger<GrpcStepPackageDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentNullException.ThrowIfNull(agentToken);
        ArgumentNullException.ThrowIfNull(extract);
        ArgumentNullException.ThrowIfNull(logger);

        _serverUrl  = serverUrl;
        _agentToken = agentToken;
        _extract    = extract;
        _logger     = logger;
    }

    /// <inheritdoc />
    public async Task EnsureExtractedAsync(string name, string version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);

        var client = GetOrCreateClient();

        _logger.LogInformation(
            "Downloading step package {Name} {Version} via gRPC…", name, version);

        var call = client.DownloadStepPackage(
            new StepPackageDownloadRequest { Name = name, Version = version },
            cancellationToken: ct);

        await DownloadAndExtractAsync(
            name, version, call.ResponseStream.ReadAllAsync(ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The streaming + hashing + extracting core, split out for unit testing.
    /// The production path feeds it the gRPC response stream; tests feed it a
    /// hand-built <see cref="IAsyncEnumerable{T}"/> so the SHA-256 contract +
    /// trailer handling can be exercised without spinning up a server.
    /// </summary>
    internal async Task DownloadAndExtractAsync(
        string name,
        string version,
        IAsyncEnumerable<StepPackageChunk> chunks,
        CancellationToken ct)
    {
        var tempArchive = Path.Combine(
            Path.GetTempPath(),
            $"kraken-step-{Guid.NewGuid():N}.kdeploy-step");

        long totalBytes    = 0;
        string? trailerSha = null;
        using var sha      = SHA256.Create();

        try
        {
            await using (var fs = new FileStream(
                tempArchive, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (chunk.IsLast)
                    {
                        trailerSha = chunk.Sha256;
                        // Trailer chunk carries no payload — break before
                        // writing/hashing so we don't perturb the digest.
                        break;
                    }

                    var data = chunk.Data.Memory;
                    if (data.Length == 0) { continue; }

                    // Hash incrementally so we never need a second pass over
                    // the (potentially large) archive once it's on disk.
                    sha.TransformBlock(data.ToArray(), 0, data.Length, null, 0);
                    await fs.WriteAsync(data, ct).ConfigureAwait(false);
                    totalBytes += data.Length;
                }
            }

            if (trailerSha is null)
            {
                throw new InvalidDataException(
                    $"Step package {name} {version}: server closed the stream without a trailer chunk.");
            }

            sha.TransformFinalBlock([], 0, 0);
            var localSha = Convert.ToHexStringLower(sha.Hash!);

            if (!string.Equals(localSha, trailerSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Step package {name} {version}: SHA-256 mismatch — server sent {trailerSha}, computed {localSha}.");
            }

            _logger.LogInformation(
                "Step package {Name} {Version} streamed ({Bytes:N0} bytes, sha256={Sha}); extracting…",
                name, version, totalBytes, localSha);

            await _extract(name, version, tempArchive).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempArchive); } catch { /* best effort */ }
        }
    }

    private StepPackageDelivery.StepPackageDeliveryClient GetOrCreateClient()
    {
        var serverUrl  = _serverUrl();
        var agentToken = _agentToken();

        if (_client is not null && _channelServerUrl == serverUrl)
        {
            return _client;
        }

        // Dispose the old channel if the server URL rotated.
        _channel?.Dispose();
        _channel = null;
        _client  = null;

        // Allow HTTP/2 over plain text for development / smoke-test envs.
        AppContext.SetSwitch(
            "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);

        _channel = GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        _client           = new StepPackageDelivery.StepPackageDeliveryClient(_channel);
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

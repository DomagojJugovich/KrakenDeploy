using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts.Grpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for the stream-handling core of <see cref="GrpcStepPackageDownloader"/>.
/// The full gRPC plumbing isn't exercised here; we drive the internal
/// <c>DownloadAndExtractAsync</c> helper with hand-built chunk streams so the
/// contract — incremental hashing, trailer-only digest, hash-mismatch
/// rejection, extract-on-success — is verifiable without standing up a server.
/// </summary>
public sealed class GrpcStepPackageDownloaderTests
{
    [Fact]
    public async Task Streams_bytes_to_temp_file_and_calls_extract_callback_on_matching_sha()
    {
        // ── Arrange ────────────────────────────────────────────────────────
        var payload = new byte[200_000];
        Random.Shared.NextBytes(payload);
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(payload));

        var extractedFromPath = (string?)null;
        var extractedBytes    = (byte[]?)null;
        var downloader = NewDownloader(extract: (name, version, archivePath) =>
        {
            extractedFromPath = archivePath;
            extractedBytes    = File.ReadAllBytes(archivePath);
            return Task.CompletedTask;
        });

        // ── Act ────────────────────────────────────────────────────────────
        await downloader.DownloadAndExtractAsync(
            "kraken.sample", "1.0.0",
            ChunksFor(payload, expectedSha, chunkSize: 64 * 1024),
            CancellationToken.None);

        // ── Assert ─────────────────────────────────────────────────────────
        extractedFromPath.Should().NotBeNull();
        extractedBytes.Should().NotBeNull();
        extractedBytes.Should().Equal(payload,
            "the extract callback receives the assembled archive on disk");
    }

    [Fact]
    public async Task Throws_when_server_omits_the_trailer_chunk()
    {
        var downloader = NewDownloader();
        await downloader.Invoking(d => d.DownloadAndExtractAsync(
                "kraken.sample", "1.0.0",
                NoTrailerChunks(new byte[] { 1, 2, 3, 4 }),
                CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*without a trailer chunk*");
    }

    [Fact]
    public async Task Throws_on_sha_mismatch_between_streamed_bytes_and_trailer()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        const string wrongSha =
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";

        var extractCalled = false;
        var downloader = NewDownloader(extract: (_, _, _) =>
        {
            extractCalled = true;
            return Task.CompletedTask;
        });

        await downloader.Invoking(d => d.DownloadAndExtractAsync(
                "kraken.sample", "1.0.0",
                ChunksFor(payload, wrongSha, chunkSize: 64),
                CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*SHA-256 mismatch*");

        extractCalled.Should().BeFalse(
            "a hash mismatch must abort before the extract callback runs — " +
            "otherwise a tampered archive would still hit the loader cache.");
    }

    [Fact]
    public async Task Empty_payload_still_succeeds_when_trailer_sha_matches_empty_hash()
    {
        var emptyHash = Convert.ToHexStringLower(SHA256.HashData([]));

        var extractedSize = -1L;
        var downloader = NewDownloader(extract: (_, _, path) =>
        {
            extractedSize = new FileInfo(path).Length;
            return Task.CompletedTask;
        });

        await downloader.DownloadAndExtractAsync(
            "kraken.sample", "1.0.0",
            ChunksFor([], emptyHash, chunkSize: 64),
            CancellationToken.None);

        extractedSize.Should().Be(0, "the trailer-only stream produces a zero-byte archive");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static GrpcStepPackageDownloader NewDownloader(
        Func<string, string, string, Task>? extract = null)
    {
        return new GrpcStepPackageDownloader(
            serverUrl:  () => "http://unused.test",
            agentToken: () => "unused",
            extract:    extract ?? ((_, _, _) => Task.CompletedTask),
            logger:     NullLogger<GrpcStepPackageDownloader>.Instance);
    }

    private static async IAsyncEnumerable<StepPackageChunk> ChunksFor(
        byte[] payload, string trailerSha, int chunkSize)
    {
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, payload.Length - offset);
            yield return new StepPackageChunk
            {
                Data       = ByteString.CopyFrom(payload, offset, len),
                TotalBytes = offset == 0 ? payload.Length : 0,
                IsLast     = false,
                Sha256     = "",
            };
            await Task.Yield();
        }

        yield return new StepPackageChunk
        {
            Data       = ByteString.Empty,
            TotalBytes = 0,
            IsLast     = true,
            Sha256     = trailerSha,
        };
    }

    private static async IAsyncEnumerable<StepPackageChunk> NoTrailerChunks(byte[] payload)
    {
        yield return new StepPackageChunk
        {
            Data       = ByteString.CopyFrom(payload),
            TotalBytes = payload.Length,
            IsLast     = false,
            Sha256     = "",
        };
        await Task.Yield();
        // No trailer chunk — the server closed the stream prematurely.
    }
}

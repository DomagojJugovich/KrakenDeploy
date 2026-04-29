using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for <see cref="PackageDeltaService"/>.
/// No database or network required — all operations are pure file I/O.
/// </summary>
public sealed class PackageDeltaServiceTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), $"kraken-delta-test-{Guid.NewGuid():N}");

    public PackageDeltaServiceTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch { /* non-fatal */ }
    }

    private static PackageDeltaService CreateService()
        => new(NullLogger<PackageDeltaService>.Instance);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a zip archive at <paramref name="path"/> containing one text file
    /// with <paramref name="content"/>.
    /// </summary>
    private static void CreateZip(string path, string content)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("payload.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildDeltaAsync_produces_delta_that_round_trips_correctly()
    {
        // Arrange: two zip archives differing only in one field — simulates a minor config change.
        var basePath = Path.Combine(_workDir, "pkg.1.0.0.zip");
        var newPath  = Path.Combine(_workDir, "pkg.1.0.1.zip");

        // Identical preamble so Octodiff can exploit the shared prefix.
        var sharedPreamble = new string('A', 4096);
        CreateZip(basePath, sharedPreamble + "version=1.0.0" + new string('Z', 4096));
        CreateZip(newPath,  sharedPreamble + "version=1.0.1" + new string('Z', 4096));

        var svc = CreateService();

        // Act: build delta
        var delta = await svc.BuildDeltaAsync(basePath, newPath, CancellationToken.None);

        delta.Should().NotBeNull();
        delta!.Length.Should().BeGreaterThan(0);

        // Act: apply delta to base → should reproduce the new zip exactly
        var outputPath = Path.Combine(_workDir, "applied.zip");
        await ApplyDelta(basePath, delta, outputPath);

        // Assert: byte-for-byte match with the target
        var newBytes     = await File.ReadAllBytesAsync(newPath);
        var appliedBytes = await File.ReadAllBytesAsync(outputPath);
        appliedBytes.Should().Equal(newBytes,
            because: "applying the Octodiff delta to the base package must reproduce the target exactly");
    }

    [Fact]
    public async Task BuildDeltaAsync_caches_signature_file_next_to_base_package()
    {
        var basePath = Path.Combine(_workDir, "pkg.1.0.0.zip");
        var newPath  = Path.Combine(_workDir, "pkg.1.0.1.zip");

        CreateZip(basePath, "base content");
        CreateZip(newPath,  "new content");

        var svc = CreateService();
        await svc.BuildDeltaAsync(basePath, newPath, CancellationToken.None);

        File.Exists(basePath + ".octosig").Should().BeTrue(
            because: "the signature file must be written alongside the base package on first build");
    }

    [Fact]
    public async Task BuildDeltaAsync_reuses_cached_signature_on_second_call()
    {
        var basePath = Path.Combine(_workDir, "pkg.1.0.0.zip");
        var newPath  = Path.Combine(_workDir, "pkg.1.0.1.zip");

        CreateZip(basePath, "some content here");
        CreateZip(newPath,  "some content changed");

        var svc = CreateService();

        // First call — builds and caches signature.
        await svc.BuildDeltaAsync(basePath, newPath, CancellationToken.None);
        var sigWriteTime = File.GetLastWriteTimeUtc(basePath + ".octosig");

        // Small pause to ensure mtime would differ if the file were rewritten.
        await Task.Delay(20);

        // Second call — must reuse the existing signature.
        await svc.BuildDeltaAsync(basePath, newPath, CancellationToken.None);
        var sigWriteTime2 = File.GetLastWriteTimeUtc(basePath + ".octosig");

        sigWriteTime2.Should().Be(sigWriteTime,
            because: "the signature file must not be rebuilt when it already exists");
    }

    [Fact]
    public async Task BuildDeltaAsync_returns_null_when_base_file_is_missing()
    {
        var basePath = Path.Combine(_workDir, "missing.zip");      // does not exist
        var newPath  = Path.Combine(_workDir, "pkg.1.0.1.zip");

        CreateZip(newPath, "some content");

        var svc   = CreateService();
        var delta = await svc.BuildDeltaAsync(basePath, newPath, CancellationToken.None);

        delta.Should().BeNull(
            because: "delta build must return null (not throw) when the base package is missing");
    }

    [Fact]
    public async Task BuildDeltaAsync_delta_is_smaller_than_full_package_for_small_change()
    {
        // Use raw (uncompressed) binary files so the file size reflects the data volume
        // rather than zip compression.  Octodiff's advantage disappears when the package
        // has already been compressed (compressed data is effectively random, preventing
        // delta exploitation of shared byte patterns).
        var sharedBlock = new string('A', 100_000);   // 100 KB identical prefix
        var basePath    = Path.Combine(_workDir, "big.1.0.0.bin");
        var newPath     = Path.Combine(_workDir, "big.1.0.1.bin");

        // Files differ only in a short suffix — ideal case for Octodiff.
        await File.WriteAllTextAsync(basePath, sharedBlock + "configValue=old");
        await File.WriteAllTextAsync(newPath,  sharedBlock + "configValue=new");

        var svc   = CreateService();
        var delta = await svc.BuildDeltaAsync(basePath, newPath, CancellationToken.None);

        var newSize = new FileInfo(newPath).Length;
        delta!.Length.Should().BeLessThan(newSize,
            because: "an Octodiff delta of a small change in a large file must transfer fewer bytes than the full file");
    }

    // ── Private helper: apply delta ───────────────────────────────────────────

    private static async Task ApplyDelta(string basePath, MemoryStream delta, string outputPath)
    {
        await Task.Run(() =>
        {
            using var basis = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // ReadWrite is required: DeltaApplier seeks back to 0 and reads the output stream
            // to verify the end-to-end hash when SkipHashCheck = false.
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            delta.Position = 0;
            new Octodiff.Core.DeltaApplier { SkipHashCheck = false }
                .Apply(basis,
                    new Octodiff.Core.BinaryDeltaReader(delta, new Octodiff.Diagnostics.NullProgressReporter()),
                    output);
        });
    }
}

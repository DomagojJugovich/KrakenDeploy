using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts.StepPackages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Tests for the Phase D-5 cache-miss-aware path in <see cref="StepPackageLoader"/>.
/// The transport is stubbed via <see cref="IStepPackageSource"/>; the loader
/// is exercised end-to-end including the post-download re-load.
/// </summary>
public sealed class StepPackageLoaderDownloadTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"kraken-loader-dl-{Guid.NewGuid():N}");

    public StepPackageLoaderDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TryLoadOrDownloadAsync_returns_null_when_no_source_is_configured()
    {
        var loader = new StepPackageLoader(
            BuildConfig(), NullLogger<StepPackageLoader>.Instance, source: null);

        var result = await loader.TryLoadOrDownloadAsync(
            "kraken.sample", "1.0.0", CancellationToken.None);

        result.Should().BeNull(
            "without an IStepPackageSource the loader cannot self-heal a cache miss");
    }

    [Fact]
    public async Task TryLoadOrDownloadAsync_pulls_from_source_then_re_loads_from_cache()
    {
        // Stage a sample archive that the stub source will hand to the loader's
        // ExtractToCache. The archive contains the test assembly under
        // executor/ so the loader resolves SamplePluginStepHandler.
        var archivePath = BuildSampleArchive(_root, "kraken.sample", "1.0.0");

        var sourceCallCount = 0;
        var loader = (StepPackageLoader?)null;
        var source = new StubStepPackageSource((name, version, ct) =>
        {
            sourceCallCount++;
            loader!.ExtractToCache(name, version, archivePath);
            return Task.CompletedTask;
        });

        loader = new StepPackageLoader(BuildConfig(), NullLogger<StepPackageLoader>.Instance, source);

        var result = await loader.TryLoadOrDownloadAsync(
            "kraken.sample", "1.0.0", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Manifest.Id.Should().Be("kraken.sample");
        sourceCallCount.Should().Be(1, "the source is consulted exactly once on cache miss");

        // Second call is a cache hit — source is NOT re-invoked.
        var again = await loader.TryLoadOrDownloadAsync(
            "kraken.sample", "1.0.0", CancellationToken.None);
        again.Should().NotBeNull();
        ReferenceEquals(again, result).Should().BeTrue();
        sourceCallCount.Should().Be(1, "the second TryLoadOrDownload hits the cache");
    }

    [Fact]
    public async Task TryLoadOrDownloadAsync_returns_null_when_source_throws()
    {
        var source = new StubStepPackageSource(
            (_, _, _) => throw new InvalidOperationException("kaboom"));
        var loader = new StepPackageLoader(
            BuildConfig(), NullLogger<StepPackageLoader>.Instance, source);

        var result = await loader.TryLoadOrDownloadAsync(
            "kraken.sample", "1.0.0", CancellationToken.None);

        result.Should().BeNull(
            "a transport failure must surface as null so the deployment can fail loudly");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"]                        = _root,
                ["StepPackages:AllowUnsignedLoads"] = "true",
            })
            .Build();

    private static string BuildSampleArchive(string workspace, string id, string version)
    {
        var archivePath = Path.Combine(workspace, $"{id}-{version}.kdeploy-step");
        var manifest = new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "Sample plug-in",
            TargetFramework  = "net10.0",
            StepTypes        = ["Kraken.Sample"],
            ExecutorAssembly =
                typeof(SamplePluginStepHandler).Assembly.GetName().Name + ".dll",
            ExecutorTypeName = typeof(SamplePluginStepHandler).FullName!,
            Signature        = "fake-base64-sig",
            SignedBy         = "kraken-project",
        };

        using var fs  = File.Create(archivePath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        var manifestEntry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
        using (var w = new StreamWriter(manifestEntry.Open()))
        {
            w.Write(StepPackageManifestJson.Serialize(manifest));
        }

        // Pack the test assembly itself as the "executor" — same trick the
        // StepPackageLoader tests use so the loader resolves a real
        // IStepHandler implementation.
        var testAssemblyPath = typeof(SamplePluginStepHandler).Assembly.Location;
        var executorEntry    = zip.CreateEntry(
            $"{StepPackageFiles.ExecutorDirectory}/{Path.GetFileName(testAssemblyPath)}");
        using (var entryStream = executorEntry.Open())
        using (var src         = File.OpenRead(testAssemblyPath))
        {
            src.CopyTo(entryStream);
        }

        return archivePath;
    }

    private sealed class StubStepPackageSource(
        Func<string, string, CancellationToken, Task> onEnsure) : IStepPackageSource
    {
        public Task EnsureExtractedAsync(string name, string version, CancellationToken ct)
            => onEnsure(name, version, ct);
    }
}

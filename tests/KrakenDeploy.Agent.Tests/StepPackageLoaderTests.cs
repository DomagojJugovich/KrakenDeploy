using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="StepPackageLoader"/> (Phase D-4). The key
/// invariants:
/// <list type="bullet">
///   <item>Missing extraction returns <c>null</c> (caller is expected to
///         download + retry).</item>
///   <item>Malformed manifest returns <c>null</c> with an error logged.</item>
///   <item>Unsigned manifest is rejected unless
///         <c>StepPackages:AllowUnsignedLoads</c> is configured.</item>
///   <item>Loaded handler type is assignable to <see cref="IStepHandler"/>
///         from the default ALC — the type-identity trap is sidestepped.</item>
///   <item><see cref="StepPackageLoader.CreateHandler"/> returns fresh
///         instances per call (per-step-execution lifecycle).</item>
/// </list>
///
/// The "executor" used in these tests is the test assembly itself — a real
/// IStepHandler implementation (<see cref="SamplePluginStepHandler"/>) lives
/// inside this test project, and the loader is pointed at the test
/// assembly's DLL on disk.
/// </summary>
public sealed class StepPackageLoaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"kraken-loader-test-{Guid.NewGuid():N}");

    public StepPackageLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Negative paths ────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_returns_null_when_extraction_directory_is_missing()
    {
        var loader = NewLoader();
        var result = loader.TryLoad("missing.package", "1.0.0");
        result.Should().BeNull();
    }

    [Fact]
    public void TryLoad_returns_null_when_manifest_id_or_version_drift_from_cache_key()
    {
        StagePackageExtract("kraken.sample", "1.0.0", manifestOverrides: m => m with
        {
            // Drift the manifest from the cache key — should be rejected.
            Id      = "different.id",
        });

        var loader = NewLoader();
        loader.TryLoad("kraken.sample", "1.0.0").Should().BeNull();
    }

    [Fact]
    public void TryLoad_returns_null_when_signature_missing_and_unsigned_loads_not_allowed()
    {
        StagePackageExtract("kraken.sample", "1.0.0", manifestOverrides: m => m with
        {
            Signature = null,
        });

        var loader = NewLoader(allowUnsignedLoads: false);
        loader.TryLoad("kraken.sample", "1.0.0").Should().BeNull();
    }

    [Fact]
    public void TryLoad_returns_null_when_executor_assembly_is_missing()
    {
        StagePackageExtract("kraken.sample", "1.0.0",
            includeExecutor: false);

        var loader = NewLoader();
        loader.TryLoad("kraken.sample", "1.0.0").Should().BeNull();
    }

    [Fact]
    public void TryLoad_returns_null_when_executorTypeName_is_unknown()
    {
        StagePackageExtract("kraken.sample", "1.0.0", manifestOverrides: m => m with
        {
            ExecutorTypeName = "KrakenDeploy.Agent.Tests.NoSuchType",
        });

        var loader = NewLoader();
        loader.TryLoad("kraken.sample", "1.0.0").Should().BeNull();
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_succeeds_and_resolves_a_real_IStepHandler_implementation()
    {
        StagePackageExtract("kraken.sample", "1.0.0");

        var loader = NewLoader();
        var loaded = loader.TryLoad("kraken.sample", "1.0.0");

        loaded.Should().NotBeNull();
        loaded!.Manifest.Id.Should().Be("kraken.sample");
        loaded.Manifest.Version.Should().Be("1.0.0");
        loaded.HandlerType.Should().NotBeNull();
        typeof(IStepHandler).IsAssignableFrom(loaded.HandlerType).Should().BeTrue(
            "the loader must reject a plug-in whose IStepHandler reference doesn't " +
            "match the agent's view — and conversely accept one that does. The ALC " +
            "delegating Contracts to the default load context is what makes this true.");
    }

    [Fact]
    public void TryLoad_is_cached_for_repeated_calls_for_the_same_version()
    {
        StagePackageExtract("kraken.sample", "1.0.0");
        var loader = NewLoader();

        var first  = loader.TryLoad("kraken.sample", "1.0.0");
        var second = loader.TryLoad("kraken.sample", "1.0.0");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeTrue(
            "the second load should return the cached LoadedPackage (same ALC, same Type)");
    }

    [Fact]
    public void CreateHandler_returns_fresh_instances_for_each_call()
    {
        // Per-step-execution lifecycle is locked — each call is a new
        // instance, so the handler's mutable state from one step cannot
        // leak into the next.
        StagePackageExtract("kraken.sample", "1.0.0");
        var loader = NewLoader();

        var a = loader.CreateHandler("kraken.sample", "1.0.0");
        var b = loader.CreateHandler("kraken.sample", "1.0.0");

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        ReferenceEquals(a, b).Should().BeFalse();
    }

    [Fact]
    public void Multiple_versions_load_into_independent_ALCs()
    {
        StagePackageExtract("kraken.sample", "1.0.0");
        StagePackageExtract("kraken.sample", "2.0.0");
        var loader = NewLoader();

        var v1 = loader.TryLoad("kraken.sample", "1.0.0");
        var v2 = loader.TryLoad("kraken.sample", "2.0.0");

        v1.Should().NotBeNull();
        v2.Should().NotBeNull();
        ReferenceEquals(v1!.Context, v2!.Context).Should().BeFalse(
            "every (name, version) loads into its own collectible AssemblyLoadContext");
    }

    [Fact]
    public void Evict_drops_the_cache_entry_so_subsequent_TryLoad_re_loads_from_disk()
    {
        StagePackageExtract("kraken.sample", "1.0.0");
        var loader = NewLoader();

        var first = loader.TryLoad("kraken.sample", "1.0.0");
        loader.Evict("kraken.sample", "1.0.0");
        var second = loader.TryLoad("kraken.sample", "1.0.0");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first, second).Should().BeFalse(
            "after Evict, TryLoad must rebuild a new LoadedPackage from disk");
    }

    // ── ExtractToCache ────────────────────────────────────────────────────

    [Fact]
    public void ExtractToCache_unpacks_an_archive_under_the_loader_cache_directory()
    {
        // Build a sample zip on the fly with a manifest only — no executor,
        // so we can verify the archive layout without the loader trying to
        // actually load anything.
        var archive = Path.Combine(_root, "sample.kdeploy-step");
        using (var fs   = File.Create(archive))
        using (var zip  = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var entry  = zip.CreateEntry(StepPackageFiles.ManifestFileName);
            using var w = new StreamWriter(entry.Open());
            w.Write(StepPackageManifestJson.Serialize(BuildManifest()));
        }

        var loader = NewLoader();
        var dir    = loader.ExtractToCache("kraken.sample", "1.0.0", archive);

        Directory.Exists(dir).Should().BeTrue();
        File.Exists(Path.Combine(dir, StepPackageFiles.ManifestFileName)).Should().BeTrue();
        File.Exists(Path.Combine(dir, "package" + StepPackageFiles.Extension)).Should().BeTrue(
            "ExtractToCache keeps the original archive alongside the extracted form");
    }

    [Fact]
    public async Task ExtractToCache_is_safe_under_concurrent_extractions()
    {
        // B7: pre-B7 this deleted + extracted IN PLACE — concurrent extractions
        // of the same version deleted each other's files mid-extract, and a
        // crash left a half-populated dir the loader's Directory.Exists hit
        // trusted. Now each extraction lands whole via a tmp-dir move; losers
        // reuse the winner's complete directory.
        var archive = Path.Combine(_root, "race.kdeploy-step");
        using (var fs  = File.Create(archive))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var entry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
            using var w = new StreamWriter(entry.Open());
            w.Write(StepPackageManifestJson.Serialize(BuildManifest()));
        }

        var loader = NewLoader();
        var dirs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => loader.ExtractToCache("kraken.race", "1.0.0", archive))));

        dirs.Distinct().Should().ContainSingle("every extraction resolves to the one cache dir");
        var dir = dirs[0];
        File.Exists(Path.Combine(dir, StepPackageFiles.ManifestFileName)).Should().BeTrue();
        File.Exists(Path.Combine(dir, "package" + StepPackageFiles.Extension)).Should().BeTrue();

        // No half-extracted temp siblings survive.
        var parent = Path.GetDirectoryName(dir)!;
        Directory.EnumerateDirectories(parent, "*.tmp-*").Should().BeEmpty(
            "temp extraction dirs are moved into place or cleaned up");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private StepPackageLoader NewLoader(bool allowUnsignedLoads = true)
        => new(BuildConfig(allowUnsignedLoads), NullLogger<StepPackageLoader>.Instance);

    private IConfiguration BuildConfig(bool allowUnsignedLoads) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"]                              = _root,
                ["StepPackages:AllowUnsignedLoads"]       = allowUnsignedLoads.ToString(),
            })
            .Build();

    private void StagePackageExtract(
        string name,
        string version,
        Func<StepPackageManifest, StepPackageManifest>? manifestOverrides = null,
        bool includeExecutor = true)
    {
        var dir = Path.Combine(_root, "step-packages-cache", name, version);
        Directory.CreateDirectory(dir);

        var manifest = BuildManifest(name, version);
        if (manifestOverrides is not null) { manifest = manifestOverrides(manifest); }
        File.WriteAllText(
            Path.Combine(dir, StepPackageFiles.ManifestFileName),
            StepPackageManifestJson.Serialize(manifest));

        if (includeExecutor)
        {
            // The "executor" for the test is the test assembly itself —
            // SamplePluginStepHandler lives below in this same project, so
            // the loader will resolve a real IStepHandler implementation.
            var executorDir = Path.Combine(dir, StepPackageFiles.ExecutorDirectory);
            Directory.CreateDirectory(executorDir);
            var testAssemblyPath = typeof(SamplePluginStepHandler).Assembly.Location;
            var targetPath = Path.Combine(executorDir,
                Path.GetFileName(testAssemblyPath));
            File.Copy(testAssemblyPath, targetPath, overwrite: true);
        }
    }

    private static StepPackageManifest BuildManifest(
        string id = "kraken.sample", string version = "1.0.0")
    {
        var testAssemblyName = typeof(SamplePluginStepHandler).Assembly.GetName().Name + ".dll";
        return new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "Sample plug-in",
            TargetFramework  = "net10.0",
            StepTypes        = ["Kraken.Sample"],
            ExecutorAssembly = testAssemblyName!,
            ExecutorTypeName = typeof(SamplePluginStepHandler).FullName!,
            // The dev sentinel — paired with AllowUnsignedLoads=true the
            // loader skips RSA verification (post-D-12). Use this instead
            // of a fake base64 string because the new verifier would
            // otherwise try (and fail) to RSA-verify it.
            Signature        = "unsigned-dev-build",
            SignedBy         = "kraken-project",
        };
    }
}

/// <summary>
/// Minimal <see cref="IStepHandler"/> used as the "executor" by the loader
/// tests. Must be a public, parameterless-ctor class so the loader's
/// <see cref="Activator.CreateInstance(Type)"/> path can construct it.
/// </summary>
public sealed class SamplePluginStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Kraken.Sample", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => false;

    public Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
        => Task.FromResult(true);
}

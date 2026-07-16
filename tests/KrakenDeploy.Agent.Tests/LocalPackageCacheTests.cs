using FluentAssertions;
using KrakenDeploy.Agent.Transport;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="LocalPackageCache"/>.
/// All operations work on a temporary directory that is cleaned up after each test.
/// </summary>
public sealed class LocalPackageCacheTests : IDisposable
{
    private readonly string _cacheRoot =
        Path.Combine(Path.GetTempPath(), $"kraken-cache-test-{Guid.NewGuid():N}");

    public LocalPackageCacheTests() => Directory.CreateDirectory(_cacheRoot);

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); }
        catch { /* non-fatal */ }
    }

    private LocalPackageCache CreateCache() => new(_cacheRoot);

    // ── TryGetCachedPath ──────────────────────────────────────────────────────

    [Fact]
    public void TryGetCachedPath_returns_null_for_cache_miss()
    {
        var cache = CreateCache();
        cache.TryGetCachedPath("MyPackage", "1.0.0").Should().BeNull();
    }

    [Fact]
    public async Task TryGetCachedPath_returns_path_after_store()
    {
        var cache      = CreateCache();
        var sourcePath = CreateTempFile("hello");

        await cache.StoreAsync("MyPackage", "1.0.0", sourcePath);

        cache.TryGetCachedPath("MyPackage", "1.0.0").Should().NotBeNull();
    }

    [Fact]
    public async Task TryGetCachedPath_cached_file_has_correct_content()
    {
        var cache      = CreateCache();
        var sourcePath = CreateTempFile("package content");

        await cache.StoreAsync("Acme.Web", "2.5.1", sourcePath);

        var cachedPath = cache.TryGetCachedPath("Acme.Web", "2.5.1");
        cachedPath.Should().NotBeNull();

        var content = await File.ReadAllTextAsync(cachedPath!);
        content.Should().Be("package content");
    }

    // ── GetCachedVersions ─────────────────────────────────────────────────────

    [Fact]
    public void GetCachedVersions_returns_empty_for_unknown_package()
    {
        var cache = CreateCache();
        cache.GetCachedVersions("Ghost.Package").Should().BeEmpty();
    }

    [Fact]
    public async Task GetCachedVersions_returns_all_stored_versions()
    {
        var cache = CreateCache();

        await cache.StoreAsync("MyApp", "1.0.0", CreateTempFile("v1"));
        await cache.StoreAsync("MyApp", "1.1.0", CreateTempFile("v1.1"));
        await cache.StoreAsync("MyApp", "2.0.0", CreateTempFile("v2"));

        var versions = cache.GetCachedVersions("MyApp");
        versions.Should().HaveCount(3)
            .And.Contain(["1.0.0", "1.1.0", "2.0.0"]);
    }

    [Fact]
    public async Task GetCachedVersions_isolates_by_packageId()
    {
        var cache = CreateCache();

        await cache.StoreAsync("PackageA", "1.0.0", CreateTempFile("a"));
        await cache.StoreAsync("PackageB", "1.0.0", CreateTempFile("b"));

        cache.GetCachedVersions("PackageA").Should().ContainSingle("1.0.0");
        cache.GetCachedVersions("PackageB").Should().ContainSingle("1.0.0");
    }

    // ── StoreAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoreAsync_is_idempotent()
    {
        var cache = CreateCache();

        await cache.StoreAsync("Pkg", "1.0.0", CreateTempFile("first"));
        await cache.StoreAsync("Pkg", "1.0.0", CreateTempFile("second"));

        var cachedPath = cache.TryGetCachedPath("Pkg", "1.0.0")!;
        var content    = await File.ReadAllTextAsync(cachedPath);
        // B7 semantics change: a (packageId, version) entry is content-addressed
        // and SHA-verified by the downloader before it is ever stored — a
        // re-store KEEPS the existing entry instead of replacing it (replacing
        // would race a concurrent extraction holding the zip open).
        content.Should().Be("first",
            because: "an existing verified entry is kept, not overwritten");
    }

    [Fact]
    public async Task StoreAsync_returns_the_cache_path()
    {
        var cache      = CreateCache();
        var sourcePath = CreateTempFile("data");

        var returned = await cache.StoreAsync("Pkg", "3.0.0", sourcePath);

        returned.Should().Be(cache.TryGetCachedPath("Pkg", "3.0.0"),
            because: "StoreAsync must return the same path as TryGetCachedPath");
    }

    // ── Path sanitisation ─────────────────────────────────────────────────────

    [Fact]
    public async Task StoreAsync_sanitises_path_traversal_in_packageId()
    {
        var cache = CreateCache();

        // A path-traversal attempt — should not escape the cache root.
        var act = async () => await cache.StoreAsync("../../../evil", "1.0.0", CreateTempFile("x"));

        // The sanitised version stores safely under cacheRoot.
        await act.Should().NotThrowAsync(because: "path-traversal characters must be sanitised, not throw");

        var cachedPath = cache.TryGetCachedPath("../../../evil", "1.0.0")!;
        cachedPath.Should().StartWith(_cacheRoot,
            because: "the cached file must remain within the cache root directory");
    }

    // ── Private helper ────────────────────────────────────────────────────────

    // ── B7: crash/concurrency safety ──────────────────────────────────────────

    [Fact]
    public async Task Concurrent_stores_and_reads_never_yield_a_torn_package()
    {
        // Pre-B7 StoreAsync truncated the FINAL path in place (FileMode.Create),
        // so TryGetCachedPath could return a half-written zip to a concurrent
        // reader. Now the entry is renamed into place whole: every hit must
        // read back the complete expected content, every time.
        var cache = CreateCache();
        var payload = new string('x', 512 * 1024); // large enough to make a torn copy observable
        var sourcePath = CreateTempFile(payload);

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var version = $"1.0.{iteration}";
            using var stop = new CancellationTokenSource();

            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var hit = cache.TryGetCachedPath("Race.Pkg", version);
                    if (hit is not null)
                    {
                        // A hit must NEVER be torn or locked-for-write.
                        var content = await File.ReadAllTextAsync(hit);
                        content.Length.Should().Be(payload.Length,
                            "a cache hit must always be the complete package");
                    }
                    await Task.Delay(1);
                }
            });

            var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(
                () => cache.StoreAsync("Race.Pkg", version, sourcePath)));
            var paths = await Task.WhenAll(writers);
            paths.Should().AllSatisfy(p => File.Exists(p).Should().BeTrue());

            stop.Cancel();
            await reader;
        }
    }

    [Fact]
    public async Task Tmp_files_are_never_reported_as_hits_or_versions()
    {
        // A crash mid-store leaves only .tmp-* siblings — they must be
        // invisible to both the hit check and version enumeration (pre-B7 a
        // crash left a truncated zip at the FINAL path, poisoning the cache).
        var cache = CreateCache();
        var sourcePath = CreateTempFile("real content");
        var realPath = await cache.StoreAsync("Crashy", "2.0.0", sourcePath);

        var orphanDir = Path.Combine(_cacheRoot, "Crashy", "3.0.0");
        Directory.CreateDirectory(orphanDir);
        await File.WriteAllTextAsync(
            Path.Combine(orphanDir, "Crashy.3.0.0.zip.tmp-deadbeef"), "half-written");

        cache.TryGetCachedPath("Crashy", "3.0.0").Should().BeNull(
            "an interrupted store must never look like a cached package");
        cache.GetCachedVersions("Crashy").Should().BeEquivalentTo(["2.0.0"]);
        File.Exists(realPath).Should().BeTrue();
    }

    [Fact]
    public async Task Storing_an_existing_entry_skips_the_rewrite()
    {
        // Entries are content-addressed by (packageId, version) and the
        // downloader verified the bytes before storing — a re-store must not
        // touch the existing file (which a concurrent extraction may have open).
        var cache = CreateCache();
        var first = await cache.StoreAsync("Dedup", "1.0.0", CreateTempFile("original"));
        var writeTime = File.GetLastWriteTimeUtc(first);

        var second = await cache.StoreAsync("Dedup", "1.0.0", CreateTempFile("different bytes"));

        second.Should().Be(first);
        File.GetLastWriteTimeUtc(first).Should().Be(writeTime,
            "the existing verified entry must not be rewritten");
        (await File.ReadAllTextAsync(first)).Should().Be("original");
    }

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_cacheRoot, $"src-{Guid.NewGuid():N}.zip");
        File.WriteAllText(path, content);
        return path;
    }
}

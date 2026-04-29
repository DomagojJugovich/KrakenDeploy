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
        await cache.StoreAsync("Pkg", "1.0.0", CreateTempFile("second"));  // overwrite

        var cachedPath = cache.TryGetCachedPath("Pkg", "1.0.0")!;
        var content    = await File.ReadAllTextAsync(cachedPath);
        content.Should().Be("second", because: "overwrite should replace the previous cache entry");
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

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_cacheRoot, $"src-{Guid.NewGuid():N}.zip");
        File.WriteAllText(path, content);
        return path;
    }
}

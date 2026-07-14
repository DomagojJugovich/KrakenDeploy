using FluentAssertions;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Storage;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// T0-5 (WP A1): an attacker-controlled multipart file name must not be able to
/// write outside the package tree. Two layers: PackageService rejects a
/// non-bare file name up front, and LocalPackageStore refuses any resolved path
/// that escapes its root (defence in depth).
/// </summary>
public sealed class LocalPackageStoreContainmentTests
{
    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "kd-pkgstore-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoreAsync_throws_when_the_resolved_path_escapes_the_root()
    {
        var root = NewTempRoot();
        try
        {
            var store = new LocalPackageStore(root, new DisabledAccountContext());
            using var content = new MemoryStream([1, 2, 3]);

            var act = () => store.StoreAsync("pkg", "1.0", "../../../../../../evil.txt", content, default);

            await act.Should().ThrowAsync<InvalidOperationException>();
            Directory.Exists(root).Should().BeFalse("nothing is created when the path escapes");
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public void GetFullPath_throws_when_the_stored_path_escapes_the_root()
    {
        var store = new LocalPackageStore(NewTempRoot(), new DisabledAccountContext());
        var act = () => store.GetFullPath("../../../../../../etc/passwd");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetFullPath_resolves_a_normal_stored_path_under_the_root()
    {
        var root = NewTempRoot();
        var store = new LocalPackageStore(root, new DisabledAccountContext());
        var full = store.GetFullPath("pkg/1.0/app.nupkg");
        full.Should().StartWith(Path.GetFullPath(root));
    }
}

/// <summary>Service-level rejection + happy path against a real DB.</summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class PackageServiceUploadSanitizationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "kd-pkgsvc-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("..\\..\\..\\wwwroot\\shell.aspx")]
    [InlineData("../../evil.txt")]
    [InlineData("sub/dir/app.nupkg")]
    public async Task UploadAsync_rejects_a_file_name_that_carries_a_path(string fileName)
    {
        var root = NewTempRoot();
        try
        {
            var svc = new PackageService(postgres, new LocalPackageStore(root, new DisabledAccountContext()), TimeProvider.System);
            using var content = new MemoryStream([1]);

            var act = () => svc.UploadAsync($"pkg-{Guid.NewGuid():N}", "1.0.0", fileName, content);

            await act.Should().ThrowAsync<ArgumentException>();
            // The guard throws before the store is touched — nothing is written.
            Directory.Exists(root).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task UploadAsync_rejects_an_absolute_path_file_name()
    {
        var root = NewTempRoot();
        try
        {
            var svc = new PackageService(postgres, new LocalPackageStore(root, new DisabledAccountContext()), TimeProvider.System);
            using var content = new MemoryStream([1]);
            var absolute = OperatingSystem.IsWindows() ? @"C:\evil.txt" : "/tmp/evil.txt";

            var act = () => svc.UploadAsync($"pkg-{Guid.NewGuid():N}", "1.0.0", absolute, content);

            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public async Task UploadAsync_accepts_a_bare_file_name_and_stores_it_under_the_root()
    {
        var root = NewTempRoot();
        try
        {
            var store = new LocalPackageStore(root, new DisabledAccountContext());
            var svc = new PackageService(postgres, store, TimeProvider.System);
            using var content = new MemoryStream([1, 2, 3]);

            var pkg = await svc.UploadAsync($"pkg-{Guid.NewGuid():N}", "1.0.0", "MyApp.1.0.0.nupkg", content);

            pkg.FileName.Should().Be("MyApp.1.0.0.nupkg");
            var full = store.GetFullPath(pkg.StoredPath);
            File.Exists(full).Should().BeTrue();
            full.Should().StartWith(Path.GetFullPath(root));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }
}

using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the Phase D-8 built-in step-package seeder.
/// Stages a temp seed directory with a real <c>.kdeploy-step</c> archive
/// (built by the test fixture from raw bytes, no external project required)
/// and asserts the seeder installs it, is idempotent on re-run, and tolerates
/// individual archive failures without aborting the whole pass.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class BuiltInStepPackageSeederTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"kraken-seed-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SeedAsync_installs_a_fresh_step_package()
    {
        var pkgName    = UniquePackageName();
        var pkgVersion = "1.0.0";
        var seedDir    = Path.Combine(_workspace, "seed");
        var dataDir    = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(seedDir);

        StagePackageZip(seedDir, pkgName, pkgVersion);

        var seeder = NewSeeder(seedDir, dataDir);
        await seeder.SeedAsync();

        await using var db = postgres.CreateContext();
        var installed = await db.StepPackages
            .FirstOrDefaultAsync(p => p.Name == pkgName && p.Version == pkgVersion);

        installed.Should().NotBeNull();
        installed!.Source.Should().Be(StepPackageSource.Preinstalled,
            "the seeder uses StepPackageSource.Preinstalled so the catalog UI " +
            "can distinguish built-ins from manual uploads or GitHub-catalog pulls");
    }

    [Fact]
    public async Task SeedAsync_is_idempotent_on_re_run()
    {
        var pkgName = UniquePackageName();
        var seedDir = Path.Combine(_workspace, "seed");
        var dataDir = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(seedDir);
        StagePackageZip(seedDir, pkgName, "1.0.0");

        var seeder = NewSeeder(seedDir, dataDir);
        await seeder.SeedAsync();
        await seeder.SeedAsync();   // second pass — must not error
        await seeder.SeedAsync();   // third pass — must not error

        await using var db = postgres.CreateContext();
        var count = await db.StepPackages
            .CountAsync(p => p.Name == pkgName);
        count.Should().Be(1, "every re-run is a cheap (name, version) lookup, not a re-install");
    }

    [Fact]
    public async Task SeedAsync_skips_archives_with_unparseable_filename()
    {
        // An archive that doesn't match the {id}-{version}.kdeploy-step
        // convention should be skipped, not break the seeder for the rest.
        var seedDir = Path.Combine(_workspace, "seed");
        var dataDir = Path.Combine(_workspace, "data");
        Directory.CreateDirectory(seedDir);

        // Bad filename — no dash.
        var badPath = Path.Combine(seedDir, "no-version-here.kdeploy-step");
        await File.WriteAllBytesAsync(badPath, [1, 2, 3]);  // junk bytes

        // A good archive alongside, to prove the bad one didn't abort the loop.
        var goodName = UniquePackageName();
        StagePackageZip(seedDir, goodName, "1.0.0");

        var seeder = NewSeeder(seedDir, dataDir);
        await seeder.SeedAsync();

        await using var db = postgres.CreateContext();
        var goodInstalled = await db.StepPackages
            .AnyAsync(p => p.Name == goodName);
        goodInstalled.Should().BeTrue(
            "the bad archive must be skipped silently, not abort the whole seed pass");
    }

    [Fact]
    public async Task SeedAsync_is_a_no_op_when_seed_directory_is_missing()
    {
        var seeder = NewSeeder(
            seedDir: Path.Combine(_workspace, "does-not-exist"),
            dataDir: Path.Combine(_workspace, "data"));

        await seeder.Invoking(s => s.SeedAsync())
            .Should().NotThrowAsync(
                "a missing seed directory is the expected state on a server with no built-ins");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private BuiltInStepPackageSeeder NewSeeder(string seedDir, string dataDir)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"]                        = dataDir,
                ["StepPackages:SeedDirectory"]      = seedDir,
                ["StepPackages:AllowUnsignedUploads"] = "true",
            })
            .Build();

        var uploadService = new StepPackageService(
            postgres, config, NullLogger<StepPackageService>.Instance);

        return new BuiltInStepPackageSeeder(
            postgres, uploadService, config,
            NullLogger<BuiltInStepPackageSeeder>.Instance);
    }

    /// <summary>
    /// Builds a minimal but valid <c>.kdeploy-step</c> zip in
    /// <paramref name="seedDir"/> named <c>{name}-{version}.kdeploy-step</c>.
    /// Contents: <c>manifest.json</c> + an empty <c>executor/Sample.dll</c>
    /// placeholder so the manifest's <c>executorAssembly</c> reference resolves.
    /// </summary>
    private static void StagePackageZip(string seedDir, string name, string version)
    {
        var archivePath = Path.Combine(seedDir, $"{name}-{version}.kdeploy-step");
        using var fs    = File.Create(archivePath);
        using var zip   = new System.IO.Compression.ZipArchive(
            fs, System.IO.Compression.ZipArchiveMode.Create);

        var manifest = $$"""
        {
          "id": "{{name}}",
          "version": "{{version}}",
          "displayName": "Test",
          "targetFramework": "net10.0",
          "stepTypes": ["Test.Step"],
          "executorAssembly": "Sample.dll",
          "executorTypeName": "Sample.Handler",
          "signedBy": "kraken-project",
          "signature": "unsigned-dev-build"
        }
        """;

        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
        {
            w.Write(manifest);
        }
        using (var w = new BinaryWriter(zip.CreateEntry("executor/Sample.dll").Open()))
        {
            // 0 bytes is fine — StepPackageService validates the manifest,
            // not the DLL contents.
        }
    }

    private static string UniquePackageName()
        => "kraken.sample." + Guid.NewGuid().ToString("N")[..8];
}

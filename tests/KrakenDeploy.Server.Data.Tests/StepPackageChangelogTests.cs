using System.IO.Compression;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Phase D-12.4: <see cref="StepPackage.ChangelogMarkdown"/> extraction at
/// upload time. Three paths:
/// <list type="bullet">
///   <item>Archive ships <c>CHANGELOG.md</c> → contents land in the DB row verbatim.</item>
///   <item>Archive has no <c>CHANGELOG.md</c> → column stays <c>null</c> (existing packages).</item>
///   <item>Archive ships a hostile-sized changelog → contents are capped + truncation marker appended.</item>
/// </list>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackageChangelogTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-changelog-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task UploadAsync_persists_CHANGELOG_md_verbatim_when_archive_ships_one()
    {
        var name = UniqueName();
        var changelog =
            "## 1.0.0 — 2026-05-21\n" +
            "- Initial release.\n" +
            "- Closes Phase D-12.4.\n";

        var archive = BuildArchive(name, "1.0.0", changelog);
        await using var stream = new MemoryStream(archive);

        var svc = NewSvc();
        var result = await svc.UploadAsync(stream);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Installed!.ChangelogMarkdown.Should().Be(changelog,
            "the manifest extraction copies the changelog bytes verbatim into the DB");

        // Round-trip via the DB to confirm persistence.
        await using var db = postgres.CreateContext();
        var row = await db.StepPackages.AsNoTracking()
            .FirstAsync(p => p.Name == name);
        row.ChangelogMarkdown.Should().Be(changelog);
    }

    [Fact]
    public async Task UploadAsync_leaves_changelog_null_when_archive_omits_CHANGELOG_md()
    {
        var name = UniqueName();
        var archive = BuildArchive(name, "1.0.0", changelog: null);
        await using var stream = new MemoryStream(archive);

        var svc = NewSvc();
        var result = await svc.UploadAsync(stream);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Installed!.ChangelogMarkdown.Should().BeNull(
            "packages without a CHANGELOG.md file get null — distinguishable from " +
            "an explicit empty changelog");
    }

    [Fact]
    public async Task UploadAsync_truncates_when_changelog_exceeds_256_KB()
    {
        // 300 KB of repeating ASCII — well past the 256 KB cap.
        var hugeChangelog = new string('x', 300 * 1024);
        var name = UniqueName();
        var archive = BuildArchive(name, "1.0.0", hugeChangelog);
        await using var stream = new MemoryStream(archive);

        var svc = NewSvc();
        var result = await svc.UploadAsync(stream);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var stored = result.Installed!.ChangelogMarkdown!;
        stored.Length.Should().BeGreaterThan(256 * 1024,
            "the truncation marker is appended after the cap, pushing total length a bit past 256 KB");
        stored.Should().Contain("…truncated at",
            "the truncation marker tells operators why the changelog ends abruptly");
        stored.Length.Should().BeLessThan(258 * 1024,
            "marker is short — total stays just past the cap, not unbounded");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueName() => "kraken.changelog-" + Guid.NewGuid().ToString("N");

    private StepPackageService NewSvc()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"]                          = _dataDir,
                ["StepPackages:AllowUnsignedUploads"] = "true",
            })
            .Build();
        return new StepPackageService(postgres, config,
            NullLogger<StepPackageService>.Instance);
    }

    /// <summary>
    /// Builds a minimal valid <c>.kdeploy-step</c> archive in-memory. The
    /// dev-mode sentinel signature lets <see cref="StepPackageService.UploadAsync"/>
    /// accept it with <c>AllowUnsignedUploads = true</c>.
    /// </summary>
    private static byte[] BuildArchive(string id, string version, string? changelog)
    {
        var manifest = new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "Changelog test",
            TargetFramework  = "net10.0",
            StepTypes        = ["Kraken.Test.Changelog"],
            ExecutorAssembly = "Stub.dll",
            ExecutorTypeName = "Stub.Handler",
            Signature        = "unsigned-dev-build",
            SignedBy         = "kraken-project",
        };

        using var ms  = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
            using (var sw = new StreamWriter(manifestEntry.Open()))
            {
                sw.Write(StepPackageManifestJson.Serialize(manifest));
            }

            // Stub executor DLL — any bytes; signature verification is bypassed
            // by the dev sentinel + AllowUnsignedUploads in NewSvc().
            var dllEntry = zip.CreateEntry($"{StepPackageFiles.ExecutorDirectory}/Stub.dll");
            using (var ds = dllEntry.Open())
            {
                ds.Write([0x4D, 0x5A, 0x00, 0x00]); // "MZ" header + padding
            }

            if (changelog is not null)
            {
                var clEntry = zip.CreateEntry(StepPackageFiles.ChangelogFileName);
                using var cs = clEntry.Open();
                cs.Write(Encoding.UTF8.GetBytes(changelog));
            }
        }
        return ms.ToArray();
    }
}

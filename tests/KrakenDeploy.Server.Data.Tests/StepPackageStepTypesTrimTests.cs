using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// SC0: the denormalised <c>step_packages.step_types</c> claim list is matched
/// by <see cref="StepPackageResolver"/> with a <c>",{type},"</c> sentinel, so a
/// padded entry (manifest authored as <c>"A, B"</c>) silently makes the type
/// unresolvable. Install must trim every claim; the resolver must then find a
/// type that arrived padded.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackageStepTypesTrimTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-steptypes-trim-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task UploadAsync_trims_padded_step_type_claims()
    {
        var name   = UniqueName();
        var padded = $"padded.{Guid.NewGuid():N}";
        var plain  = $"plain.{Guid.NewGuid():N}";

        // Mimic the pre-fix OctopusTentaclePackage manifest: second entry
        // carries the leading space the naive comma-split used to produce.
        var archive = BuildArchive(name, "1.0.0", [plain, $" {padded} "]);
        await using var stream = new MemoryStream(archive);

        var result = await NewSvc().UploadAsync(stream);
        result.Success.Should().BeTrue(result.ErrorMessage);

        await using var db = postgres.CreateContext();
        var row = await db.StepPackages.AsNoTracking().FirstAsync(p => p.Name == name);
        row.StepTypes.Should().Be($"{plain},{padded}",
            "every claim is trimmed + lower-cased at install so the resolver's " +
            "comma-sentinel match can find it");
    }

    [Fact]
    public async Task Resolver_finds_step_type_that_arrived_padded()
    {
        var name   = UniqueName();
        var padded = $"padded.{Guid.NewGuid():N}";

        var archive = BuildArchive(name, "1.0.0", [$" {padded}"]);
        await using var stream = new MemoryStream(archive);
        (await NewSvc().UploadAsync(stream)).Success.Should().BeTrue();

        var resolver = new StepPackageResolver(postgres);
        var pin = await resolver.ResolveLatestForStepTypeAsync(padded.ToUpperInvariant());

        pin.Should().NotBeNull(
            "a padded manifest claim must still resolve (case-insensitively) after install-side trimming");
        pin!.Name.Should().Be(name);
        pin.Version.Should().Be("1.0.0");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueName() => "kraken.trim-" + Guid.NewGuid().ToString("N");

    private StepPackageService NewSvc()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:DataPath"]                   = _dataDir,
                ["StepPackages:AllowUnsignedUploads"] = "true",
            })
            .Build();
        return new StepPackageService(postgres, config,
            NullLogger<StepPackageService>.Instance);
    }

    /// <summary>
    /// Builds a minimal valid <c>.kdeploy-step</c> archive in-memory with the
    /// given step-type claims (dev sentinel signature, accepted with
    /// <c>AllowUnsignedUploads = true</c>).
    /// </summary>
    private static byte[] BuildArchive(string id, string version, List<string> stepTypes)
    {
        var manifest = new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "StepTypes trim test",
            TargetFramework  = "net10.0",
            StepTypes        = [.. stepTypes],
            ExecutorAssembly = "Stub.dll",
            ExecutorTypeName = "Stub.Handler",
            Signature        = "unsigned-dev-build",
            SignedBy         = "kraken-project",
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
            using (var sw = new StreamWriter(manifestEntry.Open()))
            {
                sw.Write(StepPackageManifestJson.Serialize(manifest));
            }

            var dllEntry = zip.CreateEntry($"{StepPackageFiles.ExecutorDirectory}/Stub.dll");
            using var ds = dllEntry.Open();
            ds.Write([0x4D, 0x5A, 0x00, 0x00]); // "MZ" header + padding
        }
        return ms.ToArray();
    }
}

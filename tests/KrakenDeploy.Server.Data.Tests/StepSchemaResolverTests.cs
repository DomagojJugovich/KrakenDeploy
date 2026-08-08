using System.IO.Compression;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// SC8: the ONE schema-resolution path (SD-6) — pinned package version's
/// schema first; the registry's serving package's newest schema with an
/// operator-visible notice when the pin can't provide it; <c>null</c> when
/// nothing serves the type. Exercised through REAL installs so the registry
/// write-through and per-version schema rows are part of the assertion.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepSchemaResolverTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-resolver-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Pinned_version_schema_wins_over_the_serving_latest()
    {
        var (name, typeId, svc) = (UniqueName(), UniqueType(), NewSvc());
        (await svc.UploadAsync(Archive(name, "1.0.0", typeId, schemaTitle: "form v1")))
            .Success.Should().BeTrue();
        (await svc.UploadAsync(Archive(name, "1.1.0", typeId, schemaTitle: "form v2")))
            .Success.Should().BeTrue();

        var resolved = await NewResolver().ResolveAsync(typeId, name, "1.0.0");

        resolved.Should().NotBeNull();
        resolved!.Schema.Title.Should().Be("form v1",
            "the editor must render the form of the version the step executes with");
        resolved.SourcePackageVersion.Should().Be("1.0.0");
        resolved.Notice.Should().BeNull();
    }

    [Fact]
    public async Task Pin_without_schema_falls_back_to_serving_newest_with_a_notice()
    {
        var (name, typeId, svc) = (UniqueName(), UniqueType(), NewSvc());
        (await svc.UploadAsync(Archive(name, "1.0.0", typeId, schemaTitle: null)))
            .Success.Should().BeTrue("a pre-SC1 install ships no schema");
        (await svc.UploadAsync(Archive(name, "1.1.0", typeId, schemaTitle: "form v2")))
            .Success.Should().BeTrue();

        var resolved = await NewResolver().ResolveAsync(typeId, name, "1.0.0");

        resolved.Should().NotBeNull();
        resolved!.Schema.Title.Should().Be("form v2");
        resolved.SourcePackageVersion.Should().Be("1.1.0");
        resolved.Notice.Should().Contain("1.0.0").And.Contain("1.1.0",
            "the operator must see they are looking at another version's form");
    }

    [Fact]
    public async Task Unserved_type_resolves_to_null()
    {
        (await NewResolver().ResolveAsync($"ghost.{Guid.NewGuid():N}"))
            .Should().BeNull("the caller decides the preset/error fallback");
    }

    [Fact]
    public async Task GetSchemaAsync_is_exact_version_or_null()
    {
        var (name, typeId, svc) = (UniqueName(), UniqueType(), NewSvc());
        (await svc.UploadAsync(Archive(name, "1.0.0", typeId, schemaTitle: "only v1")))
            .Success.Should().BeTrue();

        var resolver = NewResolver();
        (await resolver.GetSchemaAsync(name, "1.0.0", typeId))!.Title.Should().Be("only v1");
        (await resolver.GetSchemaAsync(name, "9.9.9", typeId)).Should().BeNull(
            "the version-switch dropdown must not silently substitute another version's schema");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueName() => "kraken.resolver-" + Guid.NewGuid().ToString("N")[..12];
    private static string UniqueType() => "t." + Guid.NewGuid().ToString("N")[..12];

    private StepSchemaResolver NewResolver() =>
        new(postgres, NullLogger<StepSchemaResolver>.Instance);

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

    private static MemoryStream Archive(
        string id, string version, string typeId, string? schemaTitle)
    {
        var manifest = new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "Resolver test package",
            TargetFramework  = "net10.0",
            StepTypes        = [typeId],
            ExecutorAssembly = "Stub.dll",
            ExecutorTypeName = "Stub.Handler",
            Signature        = "unsigned-dev-build",
            SignedBy         = "kraken-project",
        };

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry(StepPackageFiles.ManifestFileName);
            using (var sw = new StreamWriter(manifestEntry.Open()))
            {
                sw.Write(StepPackageManifestJson.Serialize(manifest));
            }

            var dllEntry = zip.CreateEntry($"{StepPackageFiles.ExecutorDirectory}/Stub.dll");
            using (var ds = dllEntry.Open())
            {
                ds.Write([0x4D, 0x5A, 0x00, 0x00]); // "MZ" header + padding
            }

            if (schemaTitle is not null)
            {
                var entry = zip.CreateEntry(
                    $"{StepPackageFiles.UiSchemasDirectory}/{typeId.ToLowerInvariant()}.json");
                using var es = entry.Open();
                es.Write(Encoding.UTF8.GetBytes(
                    $$"""
                    { "id": "{{typeId.ToLowerInvariant()}}", "title": "{{schemaTitle}}",
                      "version": "1.0.0", "properties": {} }
                    """));
            }
        }
        ms.Position = 0;
        return ms;
    }
}

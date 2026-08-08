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
/// SC2: per-type schema storage. Installing a package extracts every
/// <c>ui/schemas/{typeId}.json</c> into <c>step_package_schemas</c> rows keyed
/// (package row, type); a legacy single <c>ui/ui-schema.json</c> falls back to
/// serving every claimed type; malformed or undeclared schema files refuse the
/// whole upload; rows die with their package. Plus: the migration's System
/// registry rows exist in a freshly migrated database.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackageSchemaStorageTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-schema-storage-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task UploadAsync_stores_one_schema_row_per_type_from_ui_schemas()
    {
        var name  = UniqueName();
        var typeA = $"a.{Guid.NewGuid():N}";
        var typeB = $"b.{Guid.NewGuid():N}";

        var archive = BuildArchive(name, "1.0.0", [typeA, typeB], perTypeSchemas: new()
        {
            [typeA] = MinimalSchema("type-a"),
            [typeB] = MinimalSchema("type-b"),
        });
        await using var stream = new MemoryStream(archive);

        var result = await NewSvc().UploadAsync(stream);
        result.Success.Should().BeTrue(result.ErrorMessage);

        await using var db = postgres.CreateContext();
        var rows = await db.StepPackageSchemas.AsNoTracking()
            .Where(s => s.StepPackageId == result.Installed!.Id)
            .OrderBy(s => s.StepType)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Select(r => r.StepType).Should().Equal(
            new[] { typeA, typeB }.OrderBy(t => t, StringComparer.Ordinal),
            "rows are keyed by the lower-cased claimed type");
        rows.Should().OnlyContain(r => r.SchemaJson.Contains("\"title\""),
            "schema JSON is stored verbatim");
    }

    [Fact]
    public async Task UploadAsync_maps_legacy_single_schema_to_every_claimed_type()
    {
        var name  = UniqueName();
        var typeA = $"a.{Guid.NewGuid():N}";
        var typeB = $"b.{Guid.NewGuid():N}";
        var legacy = MinimalSchema("legacy");

        var archive = BuildArchive(name, "1.0.0", [typeA, typeB],
            perTypeSchemas: null, legacySchema: legacy);
        await using var stream = new MemoryStream(archive);

        var result = await NewSvc().UploadAsync(stream);
        result.Success.Should().BeTrue(result.ErrorMessage);

        await using var db = postgres.CreateContext();
        var rows = await db.StepPackageSchemas.AsNoTracking()
            .Where(s => s.StepPackageId == result.Installed!.Id)
            .ToListAsync();

        rows.Should().HaveCount(2,
            "pre-SC1 packages shipped ONE schema for the whole package — it serves each claimed type");
        // jsonb normalises whitespace/key order — compare parsed content.
        foreach (var row in rows)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(row.SchemaJson);
            doc.RootElement.GetProperty("title").GetString().Should().Be("legacy");
        }
    }

    [Fact]
    public async Task UploadAsync_refuses_schema_file_for_undeclared_type()
    {
        var name = UniqueName();
        var archive = BuildArchive(name, "1.0.0", [$"a.{Guid.NewGuid():N}"], perTypeSchemas: new()
        {
            ["some.other.type"] = MinimalSchema("stray"),
        });
        await using var stream = new MemoryStream(archive);

        var result = await NewSvc().UploadAsync(stream);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no matching stepTypes entry");
    }

    [Fact]
    public async Task UploadAsync_refuses_unparseable_schema_file()
    {
        var name   = UniqueName();
        var typeId = $"a.{Guid.NewGuid():N}";
        var archive = BuildArchive(name, "1.0.0", [typeId], perTypeSchemas: new()
        {
            [typeId] = "{ this is not a schema",
        });
        await using var stream = new MemoryStream(archive);

        var result = await NewSvc().UploadAsync(stream);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a valid step UI schema");
    }

    [Fact]
    public async Task Uninstall_removes_the_package_schema_rows()
    {
        var name   = UniqueName();
        var typeId = $"a.{Guid.NewGuid():N}";
        var archive = BuildArchive(name, "1.0.0", [typeId], perTypeSchemas: new()
        {
            [typeId] = MinimalSchema("cascade"),
        });
        await using var stream = new MemoryStream(archive);

        var svc = NewSvc();
        var result = await svc.UploadAsync(stream);
        result.Success.Should().BeTrue(result.ErrorMessage);

        var uninstall = await svc.UninstallAsync(name, "1.0.0");
        uninstall.Status.Should().Be(StepPackageService.UninstallStatus.Uninstalled);

        await using var db = postgres.CreateContext();
        (await db.StepPackageSchemas.AsNoTracking()
                .AnyAsync(s => s.StepPackageId == result.Installed!.Id))
            .Should().BeFalse("schema rows cascade with their package row");
    }

    [Fact]
    public async Task Migration_seeded_the_two_System_registry_rows()
    {
        await using var db = postgres.CreateContext();

        var stepGroup = await db.StepTypes.AsNoTracking()
            .SingleAsync(t => t.TypeId == "kraken.stepgroup");
        stepGroup.ExecutionLocus.Should().Be(StepTypeExecutionLocus.Structural);
        stepGroup.Source.Should().Be(StepTypeEntrySource.System);
        stepGroup.Featured.Should().BeTrue();

        var deployRelease = await db.StepTypes.AsNoTracking()
            .SingleAsync(t => t.TypeId == "octopus.deployrelease");
        deployRelease.ExecutionLocus.Should().Be(StepTypeExecutionLocus.ServerRunner);
        deployRelease.Source.Should().Be(StepTypeEntrySource.System);
        deployRelease.ServingPackageName.Should().BeNull("System rows have no serving package");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueName() => "kraken.schemas-" + Guid.NewGuid().ToString("N");

    private static string MinimalSchema(string title) =>
        $$"""
        {
          "id": "test.schema",
          "title": "{{title}}",
          "version": "1.0.0",
          "properties": {}
        }
        """;

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

    private static byte[] BuildArchive(
        string id, string version, List<string> stepTypes,
        Dictionary<string, string>? perTypeSchemas, string? legacySchema = null)
    {
        var manifest = new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "Schema storage test",
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
            using (var ds = dllEntry.Open())
            {
                ds.Write([0x4D, 0x5A, 0x00, 0x00]); // "MZ" header + padding
            }

            foreach (var (typeId, json) in perTypeSchemas ?? [])
            {
                var entry = zip.CreateEntry(
                    $"{StepPackageFiles.UiSchemasDirectory}/{typeId.ToLowerInvariant()}.json");
                using var es = entry.Open();
                es.Write(Encoding.UTF8.GetBytes(json));
            }

            if (legacySchema is not null)
            {
                var entry = zip.CreateEntry(
                    $"{StepPackageFiles.UiDirectory}/{StepPackageFiles.UiSchemaFileName}");
                using var es = entry.Open();
                es.Write(Encoding.UTF8.GetBytes(legacySchema));
            }
        }
        return ms.ToArray();
    }
}

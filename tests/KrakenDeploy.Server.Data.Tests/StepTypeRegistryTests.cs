using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// SC3: the step-type registry as write-through of the package catalog —
/// install/uninstall keep <c>step_types</c> current (semver winner, manifest
/// metadata), System rows self-heal, and the seeder's SD-11 pass auto-upgrades
/// Preinstalled pins to the newest seeded version and sweeps the superseded
/// install.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepTypeRegistryTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-registry-test-{Guid.NewGuid():N}");
    private readonly string _seedDir =
        Path.Combine(Path.GetTempPath(), $"kraken-registry-seed-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_seedDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Install_writes_registry_rows_with_manifest_metadata_and_semver_winner()
    {
        var name  = UniqueName();
        var typeA = $"a.{Guid.NewGuid():N}";
        var typeB = $"b.{Guid.NewGuid():N}";
        var svc   = NewSvc();

        // 1.9.0 first, then 1.10.0 — a string comparison would call 1.9.0
        // "newer"; the registry must pick the true semver winner.
        (await svc.UploadAsync(Archive(name, "1.9.0",
        [
            new StepTypeDeclaration { Id = typeA, DisplayName = "Old title", Category = "old" },
            typeB,
        ]))).Success.Should().BeTrue();

        (await svc.UploadAsync(Archive(name, "1.10.0",
        [
            new StepTypeDeclaration
            {
                Id = typeA, DisplayName = "New title", Category = "new",
                Description = "Updated copy.", Featured = true,
            },
            typeB,
        ]))).Success.Should().BeTrue();

        await using var db = postgres.CreateContext();

        var rowA = await db.StepTypes.AsNoTracking().SingleAsync(t => t.TypeId == typeA);
        rowA.DisplayName.Should().Be("New title");
        rowA.Category.Should().Be("new");
        rowA.Description.Should().Be("Updated copy.");
        rowA.Featured.Should().BeTrue();
        rowA.ServingPackageName.Should().Be(name);
        rowA.ServingPackageVersion.Should().Be("1.10.0",
            "1.10.0 > 1.9.0 by semver even though it sorts lower as a string");
        rowA.Source.Should().Be(StepTypeEntrySource.Package);
        rowA.ExecutionLocus.Should().Be(StepTypeExecutionLocus.AgentPackage);

        var rowB = await db.StepTypes.AsNoTracking().SingleAsync(t => t.TypeId == typeB);
        rowB.DisplayName.Should().Be("Registry test package",
            "an id-only manifest entry falls back to the package's DisplayName");
    }

    [Fact]
    public async Task Uninstalling_the_last_claimer_removes_the_registry_row()
    {
        var name   = UniqueName();
        var typeId = $"a.{Guid.NewGuid():N}";
        var svc    = NewSvc();

        (await svc.UploadAsync(Archive(name, "1.0.0", [typeId]))).Success.Should().BeTrue();

        await using (var db = postgres.CreateContext())
        {
            (await db.StepTypes.AsNoTracking().AnyAsync(t => t.TypeId == typeId))
                .Should().BeTrue("install writes through to the registry");
        }

        (await svc.UninstallAsync(name, "1.0.0")).Status
            .Should().Be(StepPackageService.UninstallStatus.Uninstalled);

        await using (var db = postgres.CreateContext())
        {
            (await db.StepTypes.AsNoTracking().AnyAsync(t => t.TypeId == typeId))
                .Should().BeFalse("no installed package claims the type anymore");
        }
    }

    [Fact]
    public async Task Rebuild_restores_missing_System_rows()
    {
        await using (var db = postgres.CreateContext())
        {
            await db.StepTypes
                .Where(t => t.Source == StepTypeEntrySource.System)
                .ExecuteDeleteAsync();
        }

        await new StepTypeRegistry(postgres).RebuildAsync();

        await using (var db = postgres.CreateContext())
        {
            var systemIds = await db.StepTypes.AsNoTracking()
                .Where(t => t.Source == StepTypeEntrySource.System)
                .Select(t => t.TypeId)
                .ToListAsync();
            systemIds.Should().BeEquivalentTo(["kraken.stepgroup", "octopus.deployrelease"]);
        }
    }

    [Fact]
    public async Task Seeder_auto_upgrades_preinstalled_pins_and_sweeps_the_old_version()
    {
        var name   = UniqueName();
        var typeId = $"a.{Guid.NewGuid():N}";
        var svc    = NewSvc();
        var seeder = NewSeeder(svc);

        // Round 1: only v1 in the seed dir; a live step pins it.
        Directory.CreateDirectory(_seedDir);
        await File.WriteAllBytesAsync(
            Path.Combine(_seedDir, $"{name}-1.0.0.kdeploy-step"),
            Archive(name, "1.0.0", [typeId]).ToArray());
        await seeder.SeedAsync();

        var stepId = await SeedPinnedDeploymentStepAsync(name, "1.0.0");

        // Round 2: v1.1.0 arrives (a build shipped a bump); the sweep must
        // re-pin the live step and remove the superseded install.
        await File.WriteAllBytesAsync(
            Path.Combine(_seedDir, $"{name}-1.1.0.kdeploy-step"),
            Archive(name, "1.1.0", [typeId]).ToArray());
        File.Delete(Path.Combine(_seedDir, $"{name}-1.0.0.kdeploy-step"));
        await seeder.SeedAsync();

        await using var db = postgres.CreateContext();

        (await db.ProcessSteps.AsNoTracking().SingleAsync(s => s.Id == stepId))
            .StepPackageVersion.Should().Be("1.1.0", "SD-11 re-pins live steps to the seeded version");

        var versions = await db.StepPackages.AsNoTracking()
            .Where(p => p.Name == name).Select(p => p.Version).ToListAsync();
        versions.Should().Equal(["1.1.0"], "the superseded Preinstalled version is swept");

        (await db.StepTypes.AsNoTracking().SingleAsync(t => t.TypeId == typeId))
            .ServingPackageVersion.Should().Be("1.1.0");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string UniqueName() => "kraken.registry-" + Guid.NewGuid().ToString("N");

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

    private BuiltInStepPackageSeeder NewSeeder(StepPackageService svc)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StepPackages:SeedDirectory"] = _seedDir,
            })
            .Build();
        return new BuiltInStepPackageSeeder(postgres, svc, config,
            NullLogger<BuiltInStepPackageSeeder>.Instance);
    }

    private async Task<Guid> SeedPinnedDeploymentStepAsync(string pkgName, string pkgVersion)
    {
        await using var db = postgres.CreateContext();

        var slug = $"reg-{Guid.NewGuid():N}";
        var project = new Project
        {
            Name           = slug,
            Slug           = slug,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);

        var process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = project.Id };
        db.Processes.Add(process);

        var step = new ProcessStep
        {
            ProcessId          = process.Id,
            Name               = "pinned",
            StepType           = pkgName,
            PackageId          = "",
            TargetRoles        = [],
            Config             = [],
            SortOrder          = 0,
            StepPackageName    = pkgName,
            StepPackageVersion = pkgVersion,
        };
        db.ProcessSteps.Add(step);
        await db.SaveChangesAsync();
        return step.Id;
    }

    private static MemoryStream Archive(
        string id, string version, List<StepTypeDeclaration> stepTypes)
    {
        var manifest = new StepPackageManifest
        {
            Id               = id,
            Version          = version,
            DisplayName      = "Registry test package",
            TargetFramework  = "net10.0",
            StepTypes        = stepTypes,
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
            using var ds = dllEntry.Open();
            ds.Write([0x4D, 0x5A, 0x00, 0x00]); // "MZ" header + padding
        }
        ms.Position = 0;
        return ms;
    }
}

using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for Phase D-11 — <see cref="StepPackageService.UninstallAsync"/>.
/// Three outcomes: clean removal, blocked by a live <see cref="DeploymentStep"/>,
/// blocked by a frozen <see cref="StepSnapshot"/> inside a Release.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackageUninstallTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-uninstall-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task UninstallAsync_returns_NotFound_when_no_such_version()
    {
        var svc = NewSvc();
        var result = await svc.UninstallAsync("kraken.no-such-pkg", "1.0.0");
        result.Status.Should().Be(StepPackageService.UninstallStatus.NotFound);
        result.Conflicts.Should().BeNull();
    }

    [Fact]
    public async Task UninstallAsync_removes_the_row_and_disk_dir_when_no_references()
    {
        var name    = UniquePackageName();
        var version = "1.0.0";
        await SeedStepPackageAsync(name, version);
        var dirPath = Path.Combine(_dataDir, "step-packages", name, version);
        Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(Path.Combine(dirPath, "marker.txt"), "ok");

        var svc = NewSvc();
        var result = await svc.UninstallAsync(name, version);

        result.Status.Should().Be(StepPackageService.UninstallStatus.Uninstalled);
        result.Conflicts.Should().BeNull();

        await using var db = postgres.CreateContext();
        (await db.StepPackages.AnyAsync(p => p.Name == name && p.Version == version))
            .Should().BeFalse("DB row was removed");

        Directory.Exists(dirPath).Should().BeFalse(
            "the package's on-disk dir was deleted along with the DB row");
    }

    [Fact]
    public async Task UninstallAsync_is_blocked_by_a_live_deployment_step_pinned_to_the_version()
    {
        var name      = UniquePackageName();
        var version   = "1.0.0";
        await SeedStepPackageAsync(name, version);
        var (projectId, projectName) = await SeedProjectAsync();
        await SeedDeploymentStepAsync(projectId, stepType: name, pkgName: name, pkgVersion: version);

        var svc    = NewSvc();
        var result = await svc.UninstallAsync(name, version);

        result.Status.Should().Be(StepPackageService.UninstallStatus.Blocked);
        result.Conflicts.Should().NotBeNull();
        result.Conflicts!.LiveSteps.Should().ContainSingle()
            .Which.ProjectName.Should().Be(projectName);

        await using var db = postgres.CreateContext();
        (await db.StepPackages.AnyAsync(p => p.Name == name && p.Version == version))
            .Should().BeTrue("blocked uninstall must leave the DB row in place");
    }

    [Fact]
    public async Task UninstallAsync_is_blocked_by_a_release_snapshot_pinned_to_the_version()
    {
        var name    = UniquePackageName();
        var version = "1.0.0";
        await SeedStepPackageAsync(name, version);
        var (projectId, projectName) = await SeedProjectAsync();
        // Live step so ReleaseService.CreateAsync has at least one step to snapshot.
        await SeedDeploymentStepAsync(projectId, stepType: name, pkgName: name, pkgVersion: version);

        var release = await new ReleaseService(postgres, new AllowAllPermissionEvaluator())
            .CreateAsync(projectId, "1.0.0", CallerAuthorization.System);

        // Remove the live step so only the release snapshot keeps the pin alive.
        await using (var db = postgres.CreateContext())
        {
            var step = await db.ProcessSteps
                .FirstAsync(s => s.Process.OwnerKind == ProcessOwnerKind.Project && s.Process.OwnerId == projectId);
            db.ProcessSteps.Remove(step);
            await db.SaveChangesAsync();
        }

        var result = await NewSvc().UninstallAsync(name, version);

        result.Status.Should().Be(StepPackageService.UninstallStatus.Blocked);
        result.Conflicts.Should().NotBeNull();
        result.Conflicts!.LiveSteps.Should().BeEmpty(
            "the live step was deleted; only the release snapshot remains");
        result.Conflicts.ReleaseSnapshots.Should().ContainSingle()
            .Which.Should().Match<StepPackageUsageReport.ReleaseSnapshotRef>(r =>
                r.ReleaseId == release.Id
                && r.ProjectName == projectName
                && r.ReleaseVersion == "1.0.0");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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

    private async Task SeedStepPackageAsync(string name, string version)
    {
        await using var db = postgres.CreateContext();
        db.StepPackages.Add(new StepPackage
        {
            Name         = name,
            Version      = version,
            Sha256       = new string('a', 64),
            ManifestJson = "{}",
            UiSchemaJson = null,
            Source       = StepPackageSource.LocalUpload,
            StepTypes    = name,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid Id, string Name)> SeedProjectAsync()
    {
        var slug = $"unin-{Guid.NewGuid():N}";
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            Name           = slug,
            Slug           = slug,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return (project.Id, slug);
    }

    private async Task SeedDeploymentStepAsync(
        Guid projectId, string stepType, string pkgName, string pkgVersion)
    {
        await using var db = postgres.CreateContext();
        var process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = projectId };
        db.Processes.Add(process);
        await db.SaveChangesAsync();

        db.ProcessSteps.Add(new ProcessStep
        {
            ProcessId          = process.Id,
            Name               = "S1",
            StepType           = stepType,
            PackageId          = "",
            TargetRoles        = [],
            Config             = [],
            SortOrder          = 0,
            StepPackageName    = pkgName,
            StepPackageVersion = pkgVersion,
        });
        await db.SaveChangesAsync();
    }

    private static string UniquePackageName()
        => "kraken.sample." + Guid.NewGuid().ToString("N")[..8];
}

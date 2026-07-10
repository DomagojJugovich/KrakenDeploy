using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for the Phase D-10 bulk-upgrade flow on
/// <see cref="StepPackageService"/>. Covers the usage query (live
/// deployment-process + runbook-process steps grouped by pinned
/// version) and the bulk-upgrade transaction (target validation,
/// skipped-row reasons, runbook + deployment-step partition).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackageBulkUpgradeTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task GetUsageAsync_groups_deployment_and_runbook_steps_by_pinned_version()
    {
        var pkgName = UniquePackageName();
        await SeedStepPackageAsync(pkgName, "1.0.0");
        await SeedStepPackageAsync(pkgName, "2.0.0");

        var (projectId, _) = await SeedProjectAsync();
        await SeedDeploymentStepAsync(projectId, "Deploy A", pkgName, "1.0.0");
        await SeedDeploymentStepAsync(projectId, "Deploy B", pkgName, "1.0.0");
        await SeedDeploymentStepAsync(projectId, "Deploy C", pkgName, "2.0.0");
        await SeedRunbookStepAsync(projectId, "Smoke",     pkgName, "1.0.0");
        await SeedRunbookStepAsync(projectId, "Migrate",   pkgName, "2.0.0");

        var usage = await NewSvc().GetUsageAsync(pkgName);

        usage.PackageName.Should().Be(pkgName);
        usage.Groups.Should().HaveCount(2);

        var v1 = usage.Groups.Single(g => g.Version == "1.0.0");
        v1.Rows.Should().HaveCount(3);
        v1.Rows.Count(r => r.IsRunbook).Should().Be(1);
        v1.Rows.Count(r => !r.IsRunbook).Should().Be(2);

        var v2 = usage.Groups.Single(g => g.Version == "2.0.0");
        v2.Rows.Should().HaveCount(2);
        v2.Rows.Count(r => r.IsRunbook).Should().Be(1);
    }

    [Fact]
    public async Task BulkUpgradeAsync_bumps_targeted_steps_and_leaves_others_alone()
    {
        var pkgName = UniquePackageName();
        await SeedStepPackageAsync(pkgName, "1.0.0");
        await SeedStepPackageAsync(pkgName, "2.0.0");

        var (projectId, _) = await SeedProjectAsync();
        var bumpedId  = await SeedDeploymentStepAsync(projectId, "Deploy A", pkgName, "1.0.0");
        var leftAlone = await SeedDeploymentStepAsync(projectId, "Deploy B", pkgName, "1.0.0");

        var result = await NewSvc().BulkUpgradeAsync(
            pkgName, "2.0.0",
            deploymentStepIds: new[] { bumpedId },
            runbookStepIds: Array.Empty<Guid>());

        result.Touched.Should().Be(1);
        result.Skipped.Should().BeEmpty();
        result.TargetVersion.Should().Be("2.0.0");

        await using var db = postgres.CreateContext();
        (await db.ProcessSteps.FindAsync(bumpedId))!
            .StepPackageVersion.Should().Be("2.0.0");
        (await db.ProcessSteps.FindAsync(leftAlone))!
            .StepPackageVersion.Should().Be("1.0.0", "unticked rows aren't touched");
    }

    [Fact]
    public async Task BulkUpgradeAsync_handles_runbook_steps_too()
    {
        var pkgName = UniquePackageName();
        await SeedStepPackageAsync(pkgName, "1.0.0");
        await SeedStepPackageAsync(pkgName, "2.0.0");

        var (projectId, _) = await SeedProjectAsync();
        var rbStep = await SeedRunbookStepAsync(projectId, "Smoke", pkgName, "1.0.0");

        var result = await NewSvc().BulkUpgradeAsync(
            pkgName, "2.0.0",
            deploymentStepIds: Array.Empty<Guid>(),
            runbookStepIds: new[] { rbStep });

        result.Touched.Should().Be(1);

        await using var db = postgres.CreateContext();
        (await db.ProcessSteps.FindAsync(rbStep))!
            .StepPackageVersion.Should().Be("2.0.0");
    }

    [Fact]
    public async Task BulkUpgradeAsync_reports_already_target_and_not_found_in_skipped()
    {
        var pkgName = UniquePackageName();
        await SeedStepPackageAsync(pkgName, "1.0.0");
        await SeedStepPackageAsync(pkgName, "2.0.0");

        var (projectId, _) = await SeedProjectAsync();
        var alreadyOnTarget = await SeedDeploymentStepAsync(projectId, "Already", pkgName, "2.0.0");
        var ghostId         = Guid.NewGuid(); // not in DB

        var result = await NewSvc().BulkUpgradeAsync(
            pkgName, "2.0.0",
            deploymentStepIds: new[] { alreadyOnTarget, ghostId },
            runbookStepIds: Array.Empty<Guid>());

        result.Touched.Should().Be(0);
        result.Skipped.Should().HaveCount(2);
        result.Skipped.Should().Contain(s => s.StepId == alreadyOnTarget && s.Reason == "already-target");
        result.Skipped.Should().Contain(s => s.StepId == ghostId         && s.Reason == "not-found");
    }

    [Fact]
    public async Task BulkUpgradeAsync_throws_when_target_version_is_not_installed()
    {
        var pkgName = UniquePackageName();
        await SeedStepPackageAsync(pkgName, "1.0.0");
        var (projectId, _) = await SeedProjectAsync();
        var stepId = await SeedDeploymentStepAsync(projectId, "Step", pkgName, "1.0.0");

        var svc = NewSvc();
        await svc.Invoking(s => s.BulkUpgradeAsync(
                pkgName, "9.9.9",
                deploymentStepIds: new[] { stepId },
                runbookStepIds: Array.Empty<Guid>()))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not installed*");

        // Source row stays unchanged on the failed call.
        await using var db = postgres.CreateContext();
        (await db.ProcessSteps.FindAsync(stepId))!
            .StepPackageVersion.Should().Be("1.0.0");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private StepPackageService NewSvc()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"]                          = Path.Combine(
                    Path.GetTempPath(), $"kraken-bulk-{Guid.NewGuid():N}"),
                ["StepPackages:AllowUnsignedUploads"] = "true",
            })
            .Build();
        return new StepPackageService(
            postgres, config, NullLogger<StepPackageService>.Instance);
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
        var slug = $"bulk-{Guid.NewGuid():N}";
        await using var db = postgres.CreateContext();
        var project = new Project { Name = slug, Slug = slug };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return (project.Id, slug);
    }

    private async Task<Guid> SeedDeploymentStepAsync(
        Guid projectId, string stepName, string pkgName, string pkgVersion)
    {
        await using var db = postgres.CreateContext();
        var process = await db.Processes
            .FirstOrDefaultAsync(p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == projectId);
        if (process is null)
        {
            process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = projectId };
            db.Processes.Add(process);
            await db.SaveChangesAsync();
        }

        var step = new ProcessStep
        {
            ProcessId          = process.Id,
            Name               = stepName,
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

    private async Task<Guid> SeedRunbookStepAsync(
        Guid projectId, string stepName, string pkgName, string pkgVersion)
    {
        await using var db = postgres.CreateContext();
        var runbook = await db.Runbooks
            .FirstOrDefaultAsync(r => r.ProjectId == projectId);
        if (runbook is null)
        {
            runbook = new Runbook { ProjectId = projectId, Name = "RB" };
            db.Runbooks.Add(runbook);
            await db.SaveChangesAsync();
        }

        var process = await db.Processes.FirstOrDefaultAsync(
            p => p.OwnerKind == ProcessOwnerKind.Runbook && p.OwnerId == runbook.Id);
        if (process is null)
        {
            process = new Process { OwnerKind = ProcessOwnerKind.Runbook, OwnerId = runbook.Id };
            db.Processes.Add(process);
            await db.SaveChangesAsync();
        }

        var step = new ProcessStep
        {
            ProcessId          = process.Id,
            Name               = stepName,
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

    private static string UniquePackageName()
        => "kraken.bulk." + Guid.NewGuid().ToString("N")[..8];
}

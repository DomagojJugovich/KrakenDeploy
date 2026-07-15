using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// End-to-end tests for the Phase D-6 pin flow: AddStepAsync stores a
/// (name, version) pair, UpdateStepAsync re-pins on demand, and
/// ReleaseService.CreateAsync freezes the pair into the snapshot.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class StepPackagePinTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AddStepAsync_auto_resolves_when_no_explicit_pin_is_passed()
    {
        var pkgName   = UniquePackageName();
        var projectId = await SeedProjectAsync();
        await SeedStepPackageAsync(pkgName, "1.0.0", pkgName);
        await SeedStepPackageAsync(pkgName, "1.4.2", pkgName);

        var svc = new ProcessService(postgres, new AllowAllPermissionEvaluator(), new StepPackageResolver(postgres));
        var step = await svc.AddStepAsync(
            projectId, "S1", pkgName, packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System);

        step.StepPackageName.Should().Be(pkgName);
        step.StepPackageVersion.Should().Be("1.4.2",
            "auto-resolve picks the highest installed semver");
    }

    [Fact]
    public async Task AddStepAsync_honours_an_explicit_pin()
    {
        var pkgName   = UniquePackageName();
        var projectId = await SeedProjectAsync();
        await SeedStepPackageAsync(pkgName, "1.0.0", pkgName);
        await SeedStepPackageAsync(pkgName, "2.0.0", pkgName);

        var svc = new ProcessService(postgres, new AllowAllPermissionEvaluator(), new StepPackageResolver(postgres));
        var step = await svc.AddStepAsync(
            projectId, "S1", pkgName, packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System,
            stepPackageName: pkgName,
            stepPackageVersion: "1.0.0");

        step.StepPackageVersion.Should().Be("1.0.0",
            "explicit pin wins over the auto-resolved 'latest'");
    }

    [Fact]
    public async Task AddStepAsync_leaves_pin_null_when_no_installed_package_claims_the_type()
    {
        var projectId = await SeedProjectAsync();
        // No step package seeded — step type is unique to this test.
        var stepType  = "kraken.no-such-" + Guid.NewGuid().ToString("N");

        var svc = new ProcessService(postgres, new AllowAllPermissionEvaluator(), new StepPackageResolver(postgres));
        var step = await svc.AddStepAsync(
            projectId, "S1", stepType, packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System);

        step.StepPackageName.Should().BeNull();
        step.StepPackageVersion.Should().BeNull(
            "no installed package claims the step type — agent falls back to in-DI handler");
    }

    [Fact]
    public async Task UpdateStepAsync_re_pins_only_when_both_name_and_version_supplied()
    {
        var pkgName   = UniquePackageName();
        var projectId = await SeedProjectAsync();
        await SeedStepPackageAsync(pkgName, "1.0.0", pkgName);

        var svc  = new ProcessService(postgres, new AllowAllPermissionEvaluator(), new StepPackageResolver(postgres));
        var step = await svc.AddStepAsync(
            projectId, "S1", pkgName, packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System);

        step.StepPackageVersion.Should().Be("1.0.0");

        // Re-pin to a different (hypothetical) version.
        var updated = await svc.UpdateStepAsync(
            step.Id, "S1", packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System,
            stepPackageName: pkgName,
            stepPackageVersion: "2.0.0");

        updated!.StepPackageVersion.Should().Be("2.0.0");

        // Call again with neither name nor version → pin stays untouched.
        var unchanged = await svc.UpdateStepAsync(
            step.Id, "S1", packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System);

        unchanged!.StepPackageVersion.Should().Be("2.0.0",
            "calling UpdateStepAsync without pin args must not clear the existing pin");
    }

    [Fact]
    public async Task ReleaseService_copies_the_pin_into_the_snapshot()
    {
        var pkgName   = UniquePackageName();
        var projectId = await SeedProjectAsync();
        await SeedStepPackageAsync(pkgName, "1.7.0", pkgName);

        var processSvc = new ProcessService(postgres, new AllowAllPermissionEvaluator(), new StepPackageResolver(postgres));
        await processSvc.AddStepAsync(
            projectId, "S1", pkgName, packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System);

        var releaseSvc = new ReleaseService(postgres, new StepPackageResolver(postgres));
        var release    = await releaseSvc.CreateAsync(projectId, "1.0.0");

        release.ProcessSnapshot.Should().HaveCount(1);
        var snap = release.ProcessSnapshot[0];
        snap.StepPackageName.Should().Be(pkgName);
        snap.StepPackageVersion.Should().Be("1.7.0",
            "the release snapshot freezes whatever the live step had pinned");
    }

    [Fact]
    public async Task ReleaseService_re_resolves_when_live_step_has_no_pin()
    {
        var pkgName   = UniquePackageName();
        var projectId = await SeedProjectAsync();
        // Step lives with no installed package; pin is null.
        var processSvc = new ProcessService(postgres, new AllowAllPermissionEvaluator(), stepPackageResolver: null);
        await processSvc.AddStepAsync(
            projectId, "S1", pkgName, packageId: "",
            targetRoles: [], config: new Dictionary<string, string>(),
            caller: CallerAuthorization.System);

        // Install the package *after* the step was added but before the release.
        await SeedStepPackageAsync(pkgName, "0.9.0", pkgName);

        var releaseSvc = new ReleaseService(postgres, new StepPackageResolver(postgres));
        var release    = await releaseSvc.CreateAsync(projectId, "1.0.0");

        release.ProcessSnapshot[0].StepPackageName.Should().Be(pkgName);
        release.ProcessSnapshot[0].StepPackageVersion.Should().Be("0.9.0",
            "the release re-resolves 'latest installed' at snapshot time when " +
            "the live step had no pin — guarantees the release is reproducible");
    }

    private static string UniquePackageName()
        => "kraken.sample." + Guid.NewGuid().ToString("N")[..8];

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = postgres.CreateContext();
        // Steps in this test have PackageId="" so ReleaseService's primary-
        // package resolver is bypassed (the empty-string path in CreateAsync).
        // No need to seed a Package row.
        var slug    = $"p-{Guid.NewGuid():N}";
        var project = new Project
        {
            Name           = slug,
            Slug           = slug,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedStepPackageAsync(string name, string version, string stepTypes)
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
            StepTypes    = stepTypes,
        });
        await db.SaveChangesAsync();
    }
}

using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Enforcement of channel version rules at release creation (Part C): explicit
/// package versions are validated against the channel's NuGet range + tag regex,
/// auto-latest resolves the newest satisfying version, and a malformed rule is
/// rejected at channel save.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ReleaseChannelRuleTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private const string StepName = "Deploy";
    private const string PackageId = "MyApp";

    [Fact]
    public async Task Explicit_package_version_outside_the_channel_range_is_rejected()
    {
        var (projectId, channelId) = await SeedProjectChannelAsync(range: "[1.0,2.0)", tag: null);
        var svc = new ReleaseService(postgres);

        var act = async () => await svc.CreateAsync(projectId, "rel-1",
            packageVersions: new Dictionary<string, string> { [StepName] = "2.5.0" },
            channelId: channelId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("2.5.0");
    }

    [Fact]
    public async Task Explicit_package_version_inside_the_channel_range_is_accepted()
    {
        var (projectId, channelId) = await SeedProjectChannelAsync(range: "[1.0,2.0)", tag: null);
        var svc = new ReleaseService(postgres);

        var release = await svc.CreateAsync(projectId, "rel-1",
            packageVersions: new Dictionary<string, string> { [StepName] = "1.5.0" },
            channelId: channelId);

        release.ProcessSnapshot.Single(s => s.Name == StepName).PackageVersion.Should().Be("1.5.0");
    }

    [Fact]
    public async Task Prerelease_is_rejected_when_the_channel_requires_stable()
    {
        var (projectId, channelId) = await SeedProjectChannelAsync(range: null, tag: "^$");
        var svc = new ReleaseService(postgres);

        var act = async () => await svc.CreateAsync(projectId, "rel-1",
            packageVersions: new Dictionary<string, string> { [StepName] = "1.5.0-beta" },
            channelId: channelId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Auto_latest_picks_the_newest_version_that_satisfies_the_rule()
    {
        var (projectId, channelId) = await SeedProjectChannelAsync(range: null, tag: "^$");
        // Newest upload is a pre-release; the newest STABLE is 2.1.0.
        await SeedPackagesAsync(
            ("1.5.0", -3), ("2.1.0", -2), ("2.2.0-beta", -1));
        var svc = new ReleaseService(postgres);

        var release = await svc.CreateAsync(projectId, "rel-1",
            packageVersions: null, channelId: channelId);

        release.ProcessSnapshot.Single(s => s.Name == StepName).PackageVersion.Should().Be("2.1.0",
            "auto-latest must skip the newer pre-release and pin the newest stable version");
    }

    [Fact]
    public async Task Auto_latest_throws_when_no_uploaded_version_satisfies_the_rule()
    {
        var (projectId, channelId) = await SeedProjectChannelAsync(range: null, tag: "^$");
        await SeedPackagesAsync(("2.0.0-beta", -1)); // only a pre-release exists
        var svc = new ReleaseService(postgres);

        var act = async () => await svc.CreateAsync(projectId, "rel-1",
            packageVersions: null, channelId: channelId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Default_channel_rule_is_enforced_when_no_channel_is_specified()
    {
        // The CLI + "use project default" UI path send no channelId; the default
        // channel's rule must still apply.
        var (projectId, _) = await SeedProjectChannelAsync(range: "[1.0,2.0)", tag: null, isDefault: true);
        var svc = new ReleaseService(postgres);

        var act = async () => await svc.CreateAsync(projectId, "rel-1",
            packageVersions: new Dictionary<string, string> { [StepName] = "3.0.0" },
            channelId: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ChannelService_rejects_a_malformed_version_range()
    {
        var projectId = await SeedProjectWithProcessAsync();
        var channels = new ChannelService(postgres);

        var act = async () => await channels.CreateAsync(
            projectId, "Broken", isDefault: false, lifecycleId: null,
            versionRange: "[1.0,2.0", versionTag: null); // unbalanced bracket

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(Guid projectId, Guid channelId)> SeedProjectChannelAsync(
        string? range, string? tag, bool isDefault = false)
    {
        var projectId = await SeedProjectWithProcessAsync();
        var channel = await new ChannelService(postgres).CreateAsync(
            projectId, isDefault ? "Default" : $"ch-{Guid.NewGuid():N}",
            isDefault, lifecycleId: null, versionRange: range, versionTag: tag);
        return (projectId, channel.Id);
    }

    private async Task<Guid> SeedProjectWithProcessAsync()
    {
        await using var db = postgres.CreateContext();
        var slug = $"chrule-{Guid.NewGuid():N}";
        var project = new Project
        {
            Slug = slug,
            Name = slug,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = project.Id };
        db.Processes.Add(process);
        await db.SaveChangesAsync();

        db.ProcessSteps.Add(new ProcessStep
        {
            ProcessId   = process.Id,
            Name        = StepName,
            StepType    = "Octopus.TentaclePackage",
            PackageId   = PackageId,
            TargetRoles = ["web"],
            Config      = [],
            SortOrder   = 0,
        });
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedPackagesAsync(params (string Version, int HoursAgo)[] packages)
    {
        await using var db = postgres.CreateContext();
        var now = DateTimeOffset.UtcNow;
        foreach (var (version, hoursAgo) in packages)
        {
            db.Packages.Add(new Package
            {
                PackageId   = PackageId,
                Version     = version,
                FileName    = $"{PackageId}.{version}.nupkg",
                StoredPath  = $"packages/{PackageId}/{version}",
                SizeBytes   = 1024,
                UploadedUtc = now.AddHours(hoursAgo),
            });
        }
        await db.SaveChangesAsync();
    }
}

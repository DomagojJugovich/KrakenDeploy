using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B Commit 3a — integration tests for the deployment / target /
/// release / diff context builders against the shared Postgres fixture.
/// These are the kernel the MCP tools + the M11.C diagnosis job consume.
/// </summary>
[Collection("Postgres")]
public sealed class AiContextBuilderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.DeploymentLogEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentTargetAssignments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Deployments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Environments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentTargets.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeploymentContext_ListAsync_onlyFailed_filters_to_failure_states()
    {
        var (project, env, _) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0");
        await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Succeeded);
        await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Failed);
        await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.SucceededWithWarnings);

        var builder = new DeploymentContextBuilder(postgres);
        var failed = await builder.ListAsync(onlyFailed: true);

        failed.Should().HaveCount(2);
        failed.Should().OnlyContain(d =>
            d.Status == DeploymentStatus.Failed ||
            d.Status == DeploymentStatus.SucceededWithWarnings);
    }

    [Fact]
    public async Task DeploymentContext_ListAsync_filters_by_project_slug()
    {
        var (projectA, env, _) = await SeedBaseAsync(projectSlug: "alpha");
        var projectB = await SeedProjectAsync("beta");
        var relA = await SeedReleaseAsync(projectA.Id, "1.0");
        var relB = await SeedReleaseAsync(projectB.Id, "1.0");
        await SeedDeploymentAsync(relA.Id, env.Id, DeploymentStatus.Failed);
        await SeedDeploymentAsync(relB.Id, env.Id, DeploymentStatus.Failed);

        var builder = new DeploymentContextBuilder(postgres);
        var onlyAlpha = await builder.ListAsync(onlyFailed: true, projectSlug: "alpha");

        onlyAlpha.Should().ContainSingle().Which.ProjectSlug.Should().Be("alpha");
    }

    [Fact]
    public async Task DeploymentContext_GetLogTail_returns_last_lines_in_order()
    {
        var (project, env, _) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0");
        var depId = await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Failed);

        await using (var db = postgres.CreateContext())
        {
            for (var i = 0; i < 10; i++)
            {
                db.DeploymentLogEntries.Add(new DeploymentLogEntry
                {
                    DeploymentId = depId, Sequence = i, Timestamp = DateTimeOffset.UtcNow,
                    Level = "info", Message = $"line {i}",
                });
            }
            await db.SaveChangesAsync();
        }

        var builder = new DeploymentContextBuilder(postgres);
        var tail = await builder.GetLogTailAsync(depId, tailLines: 3);

        tail.Should().NotBeNull();
        tail!.TotalLogLines.Should().Be(10);
        tail.Tail.Should().HaveCount(3);
        tail.Tail.Select(l => l.Message).Should().Equal("line 7", "line 8", "line 9");
    }

    [Fact]
    public async Task TargetHealth_GetByName_reflects_status_and_last_deployment()
    {
        var (project, env, target) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0");
        await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Succeeded, target);

        var builder = new TargetHealthBuilder(postgres);
        var health = await builder.GetByNameAsync(target.Name);

        health.Should().NotBeNull();
        health!.Status.Should().Be(TargetStatus.Online.ToString());
        health.LastDeploymentStatus.Should().Be(DeploymentStatus.Succeeded.ToString());
        health.Roles.Should().Contain("web");
    }

    [Fact]
    public async Task TargetHealth_Query_filters_by_role()
    {
        await SeedBaseAsync();
        await SeedTargetAsync("db-1", roles: ["db"]);

        var builder = new TargetHealthBuilder(postgres);
        var dbTargets = await builder.QueryAsync(role: "db");

        dbTargets.Should().ContainSingle().Which.Name.Should().Be("db-1");
    }

    [Fact]
    public async Task ReleaseContext_History_is_newest_first()
    {
        var project = await SeedProjectAsync("alpha");
        await SeedReleaseAsync(project.Id, "1.0");
        await Task.Delay(10);
        await SeedReleaseAsync(project.Id, "1.1");
        await Task.Delay(10);
        await SeedReleaseAsync(project.Id, "2.0");

        var builder = new ReleaseContextBuilder(postgres);
        var history = await builder.GetHistoryAsync("alpha");

        history.Select(r => r.Version).Should().Equal("2.0", "1.1", "1.0");
    }

    [Fact]
    public async Task DeploymentDiff_surfaces_release_and_package_deltas_vs_last_green()
    {
        var (project, env, _) = await SeedBaseAsync();

        // Baseline green release with package v1.
        var relOld = await SeedReleaseAsync(project.Id, "1.0", new StepSnapshot
        {
            Id = Guid.NewGuid(), Name = "Deploy app", StepType = "Octopus.TentaclePackage",
            SortOrder = 0, PackageId = "App", PackageVersion = "1.0.0",
            Config = new Dictionary<string, string>(),
        });
        await SeedDeploymentAsync(relOld.Id, env.Id, DeploymentStatus.Succeeded);
        await Task.Delay(10);

        // New release with package bumped to v2 + a new variable.
        var relNew = await SeedReleaseAsync(project.Id, "2.0",
            new StepSnapshot
            {
                Id = Guid.NewGuid(), Name = "Deploy app", StepType = "Octopus.TentaclePackage",
                SortOrder = 0, PackageId = "App", PackageVersion = "2.0.0",
                Config = new Dictionary<string, string>(),
            });
        await using (var db = postgres.CreateContext())
        {
            var r = await db.Releases.FirstAsync(x => x.Id == relNew.Id);
            r.VariableSnapshot = [new VariableSnapshot { Name = "NewFlag", Value = "true" }];
            await db.SaveChangesAsync();
        }
        var newDepId = await SeedDeploymentAsync(relNew.Id, env.Id, DeploymentStatus.Failed);

        var builder = new DeploymentDiffBuilder(postgres);
        var diff = await builder.BuildAsync(newDepId);

        diff.Should().NotBeNull();
        diff!.HasBaseline.Should().BeTrue();
        diff.FromReleaseVersion.Should().Be("1.0");
        diff.ToReleaseVersion.Should().Be("2.0");
        diff.PackageChanges.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { StepName = "Deploy app", FromVersion = "1.0.0", ToVersion = "2.0.0" });
        diff.VariableChanges.Added.Should().Contain("NewFlag");
    }

    [Fact]
    public async Task DeploymentDiff_reports_no_baseline_for_first_deployment()
    {
        var (project, env, _) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0");
        var depId = await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Failed);

        var builder = new DeploymentDiffBuilder(postgres);
        var diff = await builder.BuildAsync(depId);

        diff.Should().NotBeNull();
        diff!.HasBaseline.Should().BeFalse();
        diff.PackageChanges.Should().BeEmpty();
    }

    // ── Seeding helpers ──────────────────────────────────────────────────

    private async Task<(Project Project, DeploymentEnvironment Env, DeploymentTarget Target)>
        SeedBaseAsync(string projectSlug = "test-proj")
    {
        var project = await SeedProjectAsync(projectSlug);
        var env = await SeedEnvironmentAsync("prod");
        var target = await SeedTargetAsync("web-1", roles: ["web"]);
        return (project, env, target);
    }

    private async Task<Project> SeedProjectAsync(string slug)
    {
        await using var db = postgres.CreateContext();
        var p = new Project { SpaceId = WellKnown.DefaultSpaceId, Name = slug, Slug = slug };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task<DeploymentEnvironment> SeedEnvironmentAsync(string name)
    {
        await using var db = postgres.CreateContext();
        var e = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = name, Slug = name, SortOrder = 1,
        };
        db.Environments.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private async Task<DeploymentTarget> SeedTargetAsync(string name, string[] roles)
    {
        await using var db = postgres.CreateContext();
        var t = new DeploymentTarget
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = name, Roles = [.. roles],
            TransportMode = TransportMode.Reverse, Status = TargetStatus.Online,
        };
        db.DeploymentTargets.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    private async Task<Release> SeedReleaseAsync(Guid projectId, string version, params StepSnapshot[] steps)
    {
        await using var db = postgres.CreateContext();
        var r = new Release
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = projectId, Version = version,
            ProcessSnapshot = [.. steps],
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private async Task<Guid> SeedDeploymentAsync(
        Guid releaseId, Guid envId, DeploymentStatus status, DeploymentTarget? target = null)
    {
        await using var db = postgres.CreateContext();
        var d = new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ReleaseId = releaseId, EnvironmentId = envId,
            Status = status, TargetId = target?.Id,
            StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow,
        };
        db.Deployments.Add(d);
        await db.SaveChangesAsync();
        if (target is not null)
        {
            db.DeploymentTargetAssignments.Add(new DeploymentTargetAssignment
            {
                DeploymentId = d.Id, TargetId = target.Id, AddedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        return d.Id;
    }
}

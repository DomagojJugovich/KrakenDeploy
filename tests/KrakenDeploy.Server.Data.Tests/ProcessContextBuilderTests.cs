using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B — integration tests for <see cref="ProcessContextBuilder"/>
/// against the shared Postgres fixture. Pins both entry points (live
/// project process + frozen release snapshot) producing the same slim
/// DTO shape: curated config summaries, resolved parent-group names,
/// server-side classification, and the drill-down fullConfigUri.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ProcessContextBuilderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentProcesses.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BuildForProject_projects_live_process_with_curated_config_and_parent_names()
    {
        var groupId = Guid.NewGuid();
        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name    = "Argosy",
            Slug    = "argosy",
        };
        await using (var db = postgres.CreateContext())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            db.DeploymentProcesses.Add(new DeploymentProcess
            {
                ProjectId = project.Id,
                Steps =
                {
                    new DeploymentStep
                    {
                        Id        = groupId,
                        Name      = "Rolling group",
                        StepType  = KrakenStepTypes.StepGroup,
                        SortOrder = 0,
                        PackageId = "",
                        Required  = false,
                        Config    = new Dictionary<string, string>
                        {
                            ["Octopus.Action.MaxParallelism"] = "2",
                        },
                    },
                    new DeploymentStep
                    {
                        Id           = Guid.NewGuid(),
                        Name         = "Deploy site",
                        StepType     = "Kraken.IIS",
                        SortOrder    = 1,
                        PackageId    = "",
                        ParentStepId = groupId,
                        TargetRoles  = ["web"],
                        Config       = new Dictionary<string, string>
                        {
                            ["Kraken.IIS.SiteName"] = "Argosy",
                        },
                    },
                    new DeploymentStep
                    {
                        Id        = Guid.NewGuid(),
                        Name      = "Notify",
                        StepType  = "Octopus.Script",
                        SortOrder = 2,
                        PackageId = "",
                        Config    = new Dictionary<string, string>
                        {
                            ["Octopus.Action.RunOnServer"]       = "true",
                            ["Octopus.Action.Script.ScriptBody"] = "Write-Host done",
                        },
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var builder = NewBuilder();
        var ctx = await builder.BuildForProjectAsync("argosy");

        ctx.Should().NotBeNull();
        ctx!.ProjectName.Should().Be("Argosy");
        ctx.ReleaseVersion.Should().BeNull(because: "live process has no release context");
        ctx.Steps.Should().HaveCount(3);

        var group = ctx.Steps[0];
        group.StepType.Should().Be(KrakenStepTypes.StepGroup);
        group.ConfigSummary["maxParallelism"].Should().Be("2");
        group.FullConfigUri.Should().Be("kraken://projects/argosy/process/steps/0/config");

        var iis = ctx.Steps[1];
        iis.ParentName.Should().Be("Rolling group", because: "the child resolves its parent group's name");
        iis.ConfigSummary["siteName"].Should().Be("Argosy");
        iis.TargetRoles.Should().Equal("web");
        iis.IsServerSide.Should().BeFalse();

        var notify = ctx.Steps[2];
        notify.IsServerSide.Should().BeTrue(because: "RunOnServer=true classifies it server-side");
        notify.ConfigSummary["scriptPreview"].Should().Be("Write-Host done");
    }

    [Fact]
    public async Task BuildForRelease_projects_frozen_snapshot()
    {
        var project = new Project
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name    = "Argosy",
            Slug    = "argosy",
        };
        await using (var db = postgres.CreateContext())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            db.Releases.Add(new Release
            {
                SpaceId   = WellKnown.DefaultSpaceId,
                ProjectId = project.Id,
                Version   = "1.4.2",
                ProcessSnapshot =
                {
                    new StepSnapshot
                    {
                        Id        = Guid.NewGuid(),
                        Name      = "Deploy",
                        StepType  = "Octopus.Script",
                        SortOrder = 0,
                        Config    = new Dictionary<string, string>
                        {
                            ["Octopus.Action.Script.Syntax"]     = "Bash",
                            ["Octopus.Action.Script.ScriptBody"] = "echo hi",
                        },
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var builder = NewBuilder();
        var ctx = await builder.BuildForReleaseAsync("argosy", "1.4.2");

        ctx.Should().NotBeNull();
        ctx!.ReleaseVersion.Should().Be("1.4.2");
        ctx.Steps.Should().ContainSingle();
        ctx.Steps[0].ConfigSummary["syntax"].Should().Be("Bash");
        ctx.Steps[0].FullConfigUri.Should()
            .Be("kraken://releases/argosy/1.4.2/steps/0/config");
    }

    [Fact]
    public async Task BuildForProject_returns_null_for_unknown_slug()
    {
        var builder = NewBuilder();
        (await builder.BuildForProjectAsync("does-not-exist")).Should().BeNull();
    }

    private ProcessContextBuilder NewBuilder()
    {
        var registry = new StepConfigCuratorRegistry(
            new IStepConfigCurator[]
            {
                new ScriptStepConfigCurator(),
                new IisStepConfigCurator(),
                new StepGroupConfigCurator(),
            },
            new DefaultStepConfigCurator());
        return new ProcessContextBuilder(postgres, registry);
    }
}

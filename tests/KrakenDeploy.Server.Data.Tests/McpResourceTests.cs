using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Mcp.Resources;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using KrakenDeploy.Server.Data.Services.Ai.Curators;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B Commit 2 — tests the MCP resource methods directly (they're plain
/// static methods with DI-resolvable params, so no MCP transport needed).
/// Pins: the process resource returns curated JSON + writes an audit row,
/// the config drill-down returns the full unredacted dict, the deployment
/// log returns ndjson, and not-found cases throw McpException + audit
/// "not-found".
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class McpResourceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        // FK-safe delete order: log entries → deployments → releases +
        // environments → process → projects.
        await db.DeploymentLogEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Deployments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Environments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentProcesses.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Project_process_resource_returns_curated_json_and_audits()
    {
        await SeedProjectWithStepAsync();
        var audit = new SpyAuditLog();

        var result = await ProcessResources.GetProjectProcessAsync(
            NewBuilder(), audit, "argosy", CancellationToken.None);

        result.MimeType.Should().Be("application/json");
        result.Uri.Should().Be("kraken://projects/argosy/process");
        var dto = JsonSerializer.Deserialize<JsonElement>(result.Text!);
        dto.GetProperty("projectName").GetString().Should().Be("Argosy");
        dto.GetProperty("steps").GetArrayLength().Should().Be(1);

        audit.Events.Should().ContainSingle()
            .Which.Should().Be((AuditEventType.McpResourceRead, "kraken://projects/argosy/process"));
    }

    [Fact]
    public async Task Project_process_resource_throws_and_audits_not_found_for_unknown_slug()
    {
        var audit = new SpyAuditLog();

        var act = async () => await ProcessResources.GetProjectProcessAsync(
            NewBuilder(), audit, "nope", CancellationToken.None);

        await act.Should().ThrowAsync<McpException>().WithMessage("*nope*");
        audit.Events.Should().ContainSingle()
            .Which.Item1.Should().Be(AuditEventType.McpResourceRead);
    }

    [Fact]
    public async Task Step_config_drill_down_returns_full_unredacted_config()
    {
        await SeedProjectWithStepAsync();
        var audit = new SpyAuditLog();

        var result = await StepConfigResources.GetProjectStepConfigAsync(
            postgres, audit, "argosy", index: 0, CancellationToken.None);

        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Text!)!;
        config["Octopus.Action.Script.ScriptBody"].Should().Be("Write-Host secret-full-body",
            because: "the drill-down returns the COMPLETE config the curator trimmed");
    }

    [Fact]
    public async Task Step_config_drill_down_throws_for_out_of_range_index()
    {
        await SeedProjectWithStepAsync();
        var audit = new SpyAuditLog();

        var act = async () => await StepConfigResources.GetProjectStepConfigAsync(
            postgres, audit, "argosy", index: 99, CancellationToken.None);

        await act.Should().ThrowAsync<McpException>().WithMessage("*out of range*");
    }

    [Fact]
    public async Task Deployment_log_resource_returns_ndjson()
    {
        var deploymentId = await SeedDeploymentWithLogAsync();
        var audit = new SpyAuditLog();

        var result = await DeploymentLogResource.GetDeploymentLogAsync(
            postgres, audit, deploymentId, CancellationToken.None);

        result.MimeType.Should().Be("application/x-ndjson");
        var lines = result.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        JsonSerializer.Deserialize<JsonElement>(lines[0]).GetProperty("message").GetString()
            .Should().Be("line one");
    }

    [Fact]
    public async Task Deployment_log_resource_throws_for_unknown_deployment()
    {
        var audit = new SpyAuditLog();

        var act = async () => await DeploymentLogResource.GetDeploymentLogAsync(
            postgres, audit, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<McpException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ProcessContextBuilder NewBuilder()
    {
        var registry = new StepConfigCuratorRegistry(
            new IStepConfigCurator[] { new ScriptStepConfigCurator() },
            new DefaultStepConfigCurator());
        return new ProcessContextBuilder(postgres, registry);
    }

    private async Task SeedProjectWithStepAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project { SpaceId = WellKnown.DefaultSpaceId, Name = "Argosy", Slug = "argosy" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        db.DeploymentProcesses.Add(new DeploymentProcess
        {
            ProjectId = project.Id,
            Steps =
            {
                new DeploymentStep
                {
                    Id        = Guid.NewGuid(),
                    Name      = "Deploy",
                    StepType  = "Octopus.Script",
                    SortOrder = 0,
                    PackageId = "",
                    Config    = new Dictionary<string, string>
                    {
                        ["Octopus.Action.Script.ScriptBody"] = "Write-Host secret-full-body",
                    },
                },
            },
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedDeploymentWithLogAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project { SpaceId = WellKnown.DefaultSpaceId, Name = "P", Slug = "p" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var release = new Core.Domain.Releases.Release
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, Version = "1.0",
        };
        db.Releases.Add(release);
        var env = new Core.Domain.Environments.DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId, Name = "e", Slug = "e", SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        var deployment = new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ReleaseId = release.Id,
            EnvironmentId = env.Id, Status = DeploymentStatus.Succeeded,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        db.DeploymentLogEntries.AddRange(
            new DeploymentLogEntry { DeploymentId = deployment.Id, Sequence = 0, Timestamp = DateTimeOffset.UtcNow, Level = "info", Message = "line one" },
            new DeploymentLogEntry { DeploymentId = deployment.Id, Sequence = 1, Timestamp = DateTimeOffset.UtcNow, Level = "info", Message = "line two" });
        await db.SaveChangesAsync();
        return deployment.Id;
    }

    private sealed class SpyAuditLog : IAuditLog
    {
        public List<(string EventType, string? SubjectId)> Events { get; } = [];

        public Task RecordAsync(
            string eventType, string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null, Guid? userId = null,
            string? userDisplay = null, CancellationToken ct = default)
        {
            Events.Add((eventType, subjectId));
            return Task.CompletedTask;
        }
    }
}

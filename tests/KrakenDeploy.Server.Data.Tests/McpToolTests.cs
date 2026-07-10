using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Mcp.Tools;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B Commit 3b — tests the MCP tool methods directly (plain static
/// methods with DI-resolvable params). Pins the slim return shapes,
/// not-found throwing, and the retry tool's deployment-creation side
/// effect.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class McpToolTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.TaskTargetAssignments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Deployments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Releases.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Environments.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.DeploymentTargets.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task list_failed_deployments_returns_only_failures()
    {
        var (project, env, _) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0");
        await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Succeeded);
        await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Failed);
        var audit = new SpyAuditLog();

        var result = await DeploymentTools.ListFailedDeploymentsAsync(
            new DeploymentContextBuilder(postgres), audit,
            environmentName: null, projectSlug: null, sinceHours: null, CancellationToken.None);

        result.Should().ContainSingle().Which.Status.Should().Be(DeploymentStatus.Failed);
        audit.Last.Should().Be((AuditEventType.McpToolInvoked, "list_failed_deployments"));
    }

    [Fact]
    public async Task get_step_config_returns_full_config_and_throws_out_of_range()
    {
        var (project, env, _) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0", new StepSnapshot
        {
            Id = Guid.NewGuid(), Name = "Deploy", StepType = "Octopus.Script", SortOrder = 0,
            Config = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptBody"] = "full body" },
        });
        var depId = await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Failed);
        var audit = new SpyAuditLog();

        var config = await DeploymentTools.GetStepConfigAsync(
            postgres, audit, depId, stepIndex: 0, CancellationToken.None);
        config["Octopus.Action.Script.ScriptBody"].Should().Be("full body");

        var act = async () => await DeploymentTools.GetStepConfigAsync(
            postgres, audit, depId, stepIndex: 5, CancellationToken.None);
        await act.Should().ThrowAsync<McpException>().WithMessage("*out of range*");
    }

    [Fact]
    public async Task get_target_health_returns_snapshot_and_throws_for_unknown()
    {
        var (_, _, target) = await SeedBaseAsync();
        var audit = new SpyAuditLog();

        var health = await TargetTools.GetTargetHealthAsync(
            new TargetHealthBuilder(postgres), audit, target.Name, CancellationToken.None);
        health.Name.Should().Be(target.Name);

        var act = async () => await TargetTools.GetTargetHealthAsync(
            new TargetHealthBuilder(postgres), audit, "ghost", CancellationToken.None);
        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task query_targets_filters_by_role()
    {
        await SeedBaseAsync();
        await using (var db = postgres.CreateContext())
        {
            db.DeploymentTargets.Add(new DeploymentTarget
            {
                SpaceId = WellKnown.DefaultSpaceId, Name = "db-1", Roles = ["db"],
                TransportMode = TransportMode.Reverse, Status = TargetStatus.Online,
            });
            await db.SaveChangesAsync();
        }
        var audit = new SpyAuditLog();

        var dbTargets = await TargetTools.QueryTargetsAsync(
            new TargetHealthBuilder(postgres), audit, role: "db", environmentName: null, CancellationToken.None);

        dbTargets.Should().ContainSingle().Which.Name.Should().Be("db-1");
    }

    [Fact]
    public async Task get_release_history_returns_newest_first()
    {
        var project = await SeedProjectAsync("alpha");
        await SeedReleaseAsync(project.Id, "1.0");
        await Task.Delay(10);
        await SeedReleaseAsync(project.Id, "2.0");
        var audit = new SpyAuditLog();

        var history = await ReleaseTools.GetReleaseHistoryAsync(
            new ReleaseContextBuilder(postgres), audit, "alpha", count: 0, CancellationToken.None);

        history.Select(r => r.Version).Should().Equal("2.0", "1.0");
    }

    [Fact]
    public async Task retry_deployment_creates_a_new_deployment()
    {
        var (project, env, target) = await SeedBaseAsync();
        var release = await SeedReleaseAsync(project.Id, "1.0");
        var sourceId = await SeedDeploymentAsync(release.Id, env.Id, DeploymentStatus.Failed, target);
        var audit = new SpyAuditLog();
        var queue = Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>();
        var service = new DeploymentService(postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext());

        var result = await DeploymentTools.RetryDeploymentAsync(
            postgres, service, new AllowAllEvaluator(), AuthedAccessor(), audit,
            sourceId, CancellationToken.None);

        result.NewDeploymentId.Should().NotBe(sourceId);
        result.SourceDeploymentId.Should().Be(sourceId);

        await using var db = postgres.CreateContext();
        var newDep = await db.Deployments
            .Include(d => d.Targets)
            .FirstOrDefaultAsync(d => d.Id == result.NewDeploymentId);
        newDep.Should().NotBeNull();
        newDep!.ReleaseId.Should().Be(release.Id);
        newDep.Targets.Select(a => a.TargetId).Should().BeEquivalentTo([target.Id],
            "the retry must reproduce the source's target set via the assignments join");
        audit.Last.Item1.Should().Be(AuditEventType.McpToolInvoked);
    }

    [Fact]
    public async Task retry_deployment_throws_for_unknown_id()
    {
        var queue = Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>();
        var service = new DeploymentService(postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext());

        var act = async () => await DeploymentTools.RetryDeploymentAsync(
            postgres, service, new AllowAllEvaluator(), AuthedAccessor(), new SpyAuditLog(),
            Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task retry_deployment_denies_a_caller_without_DeploymentCreate()
    {
        // The M11.B deferral, closed: the tool description always CLAIMED
        // enforcement — now it exists. A principal whose evaluator denies
        // DeploymentCreate must be rejected before any DB read.
        var queue = Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>();
        var service = new DeploymentService(postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext());
        var audit = new SpyAuditLog();

        var act = async () => await DeploymentTools.RetryDeploymentAsync(
            postgres, service, new DenyAllEvaluator(), AuthedAccessor(), audit,
            Guid.NewGuid(), CancellationToken.None);

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*DeploymentCreate*");
        audit.LastDetails.Should().Contain("permission-denied");
    }

    [Fact]
    public async Task retry_deployment_rejects_an_anonymous_caller()
    {
        var queue = Channel.CreateUnbounded<KrakenDeploy.Server.Data.TenantWorkItem>();
        var service = new DeploymentService(postgres, queue, TimeProvider.System,
            new KrakenDeploy.Server.Data.Accounts.DisabledAccountContext());

        var anonymous = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity()),
            },
        };

        var act = async () => await DeploymentTools.RetryDeploymentAsync(
            postgres, service, new AllowAllEvaluator(), anonymous, new SpyAuditLog(),
            Guid.NewGuid(), CancellationToken.None);

        (await act.Should().ThrowAsync<McpException>())
            .WithMessage("*no authenticated principal*");
    }

    // ── Auth fakes for the gated tool ─────────────────────────────────────

    private static HttpContextAccessor AuthedAccessor() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Name, "mcp-test@laus.hr"),
            ], authenticationType: "test")),
        },
    };

    private sealed class AllowAllEvaluator : IPermissionEvaluator
    {
        public Task<bool> HasPermissionAsync(
            ClaimsPrincipal user, Permission permission, PermissionScope scope = default,
            bool bypassCache = false, CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
            ClaimsPrincipal user, PermissionScope scope = default, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Permission>>(
                new HashSet<Permission>(Enum.GetValues<Permission>()));

        public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
            ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    private sealed class DenyAllEvaluator : IPermissionEvaluator
    {
        public Task<bool> HasPermissionAsync(
            ClaimsPrincipal user, Permission permission, PermissionScope scope = default,
            bool bypassCache = false, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
            ClaimsPrincipal user, PermissionScope scope = default, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

        public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
            ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    // ── Seeding ──────────────────────────────────────────────────────────

    private async Task<(Project, DeploymentEnvironment, DeploymentTarget)> SeedBaseAsync()
    {
        var project = await SeedProjectAsync("test-proj");
        DeploymentEnvironment env;
        DeploymentTarget target;
        await using (var db = postgres.CreateContext())
        {
            env = new DeploymentEnvironment
            {
                SpaceId = WellKnown.DefaultSpaceId, Name = "prod", Slug = "prod", SortOrder = 1,
            };
            target = new DeploymentTarget
            {
                SpaceId = WellKnown.DefaultSpaceId, Name = "web-1", Roles = ["web"],
                TransportMode = TransportMode.Reverse, Status = TargetStatus.Online,
            };
            db.Environments.Add(env);
            db.DeploymentTargets.Add(target);
            await db.SaveChangesAsync();
        }
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

    private async Task<Release> SeedReleaseAsync(Guid projectId, string version, params StepSnapshot[] steps)
    {
        await using var db = postgres.CreateContext();
        var r = new Release
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = projectId, Version = version,
            ProcessSnapshot = [.. steps], VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private async Task<Guid> SeedDeploymentAsync(
        Guid releaseId, Guid envId, DeploymentStatus status, DeploymentTarget? target = null)
    {
        await using var db = postgres.CreateContext();
        var projectId = await db.Releases
            .Where(r => r.Id == releaseId).Select(r => r.ProjectId).FirstAsync();
        var d = new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = projectId,
            ReleaseId = releaseId, EnvironmentId = envId,
            Status = status,
            StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow,
        };
        db.Deployments.Add(d);
        await db.SaveChangesAsync();
        if (target is not null)
        {
            db.TaskTargetAssignments.Add(new TaskTargetAssignment
            {
                TaskId = d.Id, TargetId = target.Id, AddedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        return d.Id;
    }

    private sealed class SpyAuditLog : IAuditLog
    {
        public (string, string?) Last { get; private set; }
        public string? LastDetails { get; private set; }

        public Task RecordAsync(
            string eventType, string? subjectType = null, string? subjectId = null,
            string? subjectName = null, string? details = null, Guid? userId = null,
            string? userDisplay = null, CancellationToken ct = default)
        {
            Last = (eventType, subjectId);
            LastDetails = details;
            return Task.CompletedTask;
        }
    }
}

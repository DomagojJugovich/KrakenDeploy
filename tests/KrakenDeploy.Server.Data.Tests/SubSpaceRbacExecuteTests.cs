using System.Security.Claims;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Data.Accounts;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// A4 (T1-8) acceptance: sub-Space RBAC is enforced at the AUTHORITATIVE service
/// layer, where REST, CLI, and MCP all converge (the REST endpoint and the MCP
/// tool are thin adapters that call the same <c>DeploymentService.CreateAsync</c>
/// / <c>RunbookService.TriggerAsync</c>). A user granted DeploymentCreate scoped
/// to Environment=Test must be REJECTED deploying to Prod; a matching scope is
/// allowed; a system-initiated call bypasses the check. Drives the REAL
/// <see cref="PermissionEvaluator"/> against seeded role assignments.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SubSpaceRbacExecuteTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "Test"));

    [Fact]
    public async Task Deploy_is_rejected_to_an_environment_outside_the_users_scope()
    {
        var g = await SeedEnvScopedDeploymentCreateAsync();
        var svc = NewDeploymentService();

        // Deploy to PROD (outside the Env=Test grant) → denied.
        var deployProd = () => svc.CreateAsync(
            releaseId: g.ReleaseId, environmentId: g.ProdEnvId, targetId: g.TargetId,
            initiator: TaskInitiator.Api(g.UserId, "test"),
            caller: CallerAuthorization.ForUser(User(g.UserId)));
        await deployProd.Should().ThrowAsync<AuthorizationException>(
            "a DeploymentCreate grant scoped to Environment=Test must not deploy to Prod");
    }

    [Fact]
    public async Task Deploy_is_allowed_to_an_environment_inside_the_users_scope()
    {
        var g = await SeedEnvScopedDeploymentCreateAsync();
        var svc = NewDeploymentService();

        // Deploy to TEST (inside the grant) → allowed.
        var deployment = await svc.CreateAsync(
            releaseId: g.ReleaseId, environmentId: g.TestEnvId, targetId: g.TargetId,
            initiator: TaskInitiator.Api(g.UserId, "test"),
            caller: CallerAuthorization.ForUser(User(g.UserId)));

        deployment.EnvironmentId.Should().Be(g.TestEnvId);
    }

    [Fact]
    public async Task System_initiated_deploy_bypasses_the_scope_check()
    {
        var g = await SeedEnvScopedDeploymentCreateAsync();
        var svc = NewDeploymentService();

        // A system caller (e.g. parent DeployRelease step) deploys to Prod even
        // though the human's grant would not — it was authorized at origin.
        var deployment = await svc.CreateAsync(
            releaseId: g.ReleaseId, environmentId: g.ProdEnvId, targetId: g.TargetId,
            initiator: TaskInitiator.ParentStep(g.UserId, "test", Guid.NewGuid()),
            caller: CallerAuthorization.System);

        deployment.EnvironmentId.Should().Be(g.ProdEnvId);
    }

    [Fact]
    public async Task Runbook_run_is_rejected_to_an_environment_outside_the_users_scope()
    {
        var g = await SeedEnvScopedRunbookRunCreateAsync();
        var svc = NewRunbookService();

        var runProd = () => svc.TriggerAsync(
            g.RunbookId, g.ProdEnvId, g.TargetId,
            initiator: TaskInitiator.Api(g.UserId, "test"),
            caller: CallerAuthorization.ForUser(User(g.UserId)));
        await runProd.Should().ThrowAsync<AuthorizationException>(
            "a RunbookRunCreate grant scoped to Environment=Test must not run against Prod");
    }

    [Fact]
    public async Task Runbook_run_is_allowed_to_an_environment_inside_the_users_scope()
    {
        var g = await SeedEnvScopedRunbookRunCreateAsync();
        var svc = NewRunbookService();

        var run = await svc.TriggerAsync(
            g.RunbookId, g.TestEnvId, g.TargetId,
            initiator: TaskInitiator.Api(g.UserId, "test"),
            caller: CallerAuthorization.ForUser(User(g.UserId)));

        run.EnvironmentId.Should().Be(g.TestEnvId);
    }

    // ── Service factories (real evaluator) ───────────────────────────────────

    private DeploymentService NewDeploymentService() =>
        new(postgres, Channel.CreateUnbounded<TenantWorkItem>(), TimeProvider.System,
            new DisabledAccountContext(), new PermissionEvaluator(postgres, TimeProvider.System));

    private RunbookService NewRunbookService() =>
        new(postgres, new RunbookRunChannel(), TimeProvider.System,
            new DisabledAccountContext(), new PermissionEvaluator(postgres, TimeProvider.System));

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record DeployGraph(
        Guid UserId, Guid ReleaseId, Guid TestEnvId, Guid ProdEnvId, Guid TargetId);

    private async Task<DeployGraph> SeedEnvScopedDeploymentCreateAsync()
    {
        var userId = Guid.NewGuid();
        await using var db = postgres.CreateContext();
        var space = WellKnown.DefaultSpaceId;

        var testEnv = new DeploymentEnvironment { Name = "Test", Slug = $"test-{Guid.NewGuid():N}", SortOrder = 1 };
        var prodEnv = new DeploymentEnvironment { Name = "Prod", Slug = $"prod-{Guid.NewGuid():N}", SortOrder = 2 };
        var project = new Project
        {
            Name = "P", Slug = $"p-{Guid.NewGuid():N}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, space),
        };
        var target = new DeploymentTarget
        {
            Name = $"tgt-{Guid.NewGuid():N}", Roles = ["web"], TransportMode = TransportMode.Reverse,
        };
        db.Environments.AddRange(testEnv, prodEnv);
        db.Projects.Add(project);
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var release = new Release
        {
            ProjectId = project.Id, Version = "1.0.0",
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);

        await SeedEnvScopedGrantAsync(db, space, userId, Permission.DeploymentCreate, testEnv.Id);
        await db.SaveChangesAsync();

        return new DeployGraph(userId, release.Id, testEnv.Id, prodEnv.Id, target.Id);
    }

    private sealed record RunbookGraph(
        Guid UserId, Guid RunbookId, Guid TestEnvId, Guid ProdEnvId, Guid TargetId);

    private async Task<RunbookGraph> SeedEnvScopedRunbookRunCreateAsync()
    {
        var userId = Guid.NewGuid();
        await using var db = postgres.CreateContext();
        var space = WellKnown.DefaultSpaceId;

        var testEnv = new DeploymentEnvironment { Name = "Test", Slug = $"test-{Guid.NewGuid():N}", SortOrder = 1 };
        var prodEnv = new DeploymentEnvironment { Name = "Prod", Slug = $"prod-{Guid.NewGuid():N}", SortOrder = 2 };
        var project = new Project
        {
            Name = "P", Slug = $"p-{Guid.NewGuid():N}",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, space),
        };
        var target = new DeploymentTarget
        {
            Name = $"tgt-{Guid.NewGuid():N}", Roles = ["web"], TransportMode = TransportMode.Reverse,
        };
        db.Environments.AddRange(testEnv, prodEnv);
        db.Projects.Add(project);
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();

        var runbook = new Runbook { Name = "RB", ProjectId = project.Id };
        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();

        // A runbook run requires at least one step in the owning process.
        var process = new Process { OwnerKind = ProcessOwnerKind.Runbook, OwnerId = runbook.Id };
        db.Processes.Add(process);
        await db.SaveChangesAsync();
        db.ProcessSteps.Add(new ProcessStep
        {
            ProcessId = process.Id, Name = "Run", StepType = "Kraken.Script",
            PackageId = "", TargetRoles = [], Config = [], SortOrder = 0,
        });

        await SeedEnvScopedGrantAsync(db, space, userId, Permission.RunbookRunCreate, testEnv.Id);
        await db.SaveChangesAsync();

        return new RunbookGraph(userId, runbook.Id, testEnv.Id, prodEnv.Id, target.Id);
    }

    /// <summary>
    /// Grants <paramref name="permission"/> to <paramref name="userId"/> via a team,
    /// scoped to Environment=<paramref name="environmentId"/> in <paramref name="space"/>.
    /// </summary>
    private static async Task SeedEnvScopedGrantAsync(
        KrakenDbContext db, Guid space, Guid userId, Permission permission, Guid environmentId)
    {
        var role = new Role
        {
            Name = $"env-scoped-{Guid.NewGuid():N}",
            GrantedPermissions = [permission],
        };
        var team = new Team { Name = $"team-{Guid.NewGuid():N}", SpaceId = space };
        db.Roles.Add(role);
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        db.RoleAssignments.Add(new RoleAssignment
        {
            TeamId = team.Id,
            RoleId = role.Id,
            SpaceId = space,
            Scopes = [new RoleAssignmentScope { EnvironmentId = environmentId }],
        });
        await TestData.EnsureUserAsync(db, userId);
        db.Add(new TeamMember { TeamId = team.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
    }
}

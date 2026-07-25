using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP5 item 1 — target decommission. Retire is a soft-decommission (hidden from
/// matching/dispatch, agent rejected at connect, history preserved); hard delete
/// is refused while execution history references the target (RESTRICT FKs on
/// task_target_assignments / task_step_outcomes) and succeeds only for a
/// history-free target.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TargetServiceDecommissionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private TargetService NewService() =>
        new(postgres, new AllowAllPermissionEvaluator());

    // ── Retire ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetireAsync_sets_flag_status_and_bumps_token_version()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();

        var ok = await svc.RetireAsync(id, CallerAuthorization.System);

        ok.Should().BeTrue();
        await using var db = postgres.CreateContext();
        var t = await db.DeploymentTargets.FirstAsync(x => x.Id == id);
        t.IsRetired.Should().BeTrue();
        t.Status.Should().Be(TargetStatus.Disabled,
            "retiring flips the status so the fleet summary and UI show it as decommissioned");
        t.AgentTokenVersion.Should().Be(1,
            "the token bump revokes outstanding agent tokens; the AgentHub retired gate then refuses reconnects");
    }

    [Fact]
    public async Task RetireAsync_is_idempotent_and_does_not_rebump_token()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();
        await svc.RetireAsync(id, CallerAuthorization.System);

        var ok = await svc.RetireAsync(id, CallerAuthorization.System);

        ok.Should().BeTrue();
        await using var db = postgres.CreateContext();
        (await db.DeploymentTargets.FirstAsync(x => x.Id == id)).AgentTokenVersion
            .Should().Be(1, "a no-op retire must not churn the token version again");
    }

    [Fact]
    public async Task RetireAsync_returns_false_for_missing_target()
    {
        var svc = NewService();
        (await svc.RetireAsync(Guid.NewGuid(), CallerAuthorization.System)).Should().BeFalse();
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_removes_a_history_free_target()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();

        var ok = await svc.DeleteAsync(id, CallerAuthorization.System);

        ok.Should().BeTrue();
        await using var db = postgres.CreateContext();
        (await db.DeploymentTargets.IgnoreQueryFilters().AnyAsync(x => x.Id == id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_is_refused_while_a_target_assignment_exists()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();
        await SeedDeploymentWithAssignmentAsync(id);

        var act = () => svc.DeleteAsync(id, CallerAuthorization.System);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("history");

        await using var db = postgres.CreateContext();
        (await db.DeploymentTargets.IgnoreQueryFilters().AnyAsync(x => x.Id == id))
            .Should().BeTrue("a target with execution history must be preserved");
    }

    [Fact]
    public async Task DeleteAsync_is_refused_while_a_step_outcome_references_the_target()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();
        await SeedDeploymentWithStepOutcomeAsync(id);

        var act = () => svc.DeleteAsync(id, CallerAuthorization.System);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("history");
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_missing_target()
    {
        var svc = NewService();
        (await svc.DeleteAsync(Guid.NewGuid(), CallerAuthorization.System)).Should().BeFalse();
    }

    // ── Authorization (T1-8 scope check) ────────────────────────────────────

    [Fact]
    public async Task RetireAsync_throws_for_an_unauthorized_user_caller()
    {
        var svc = new TargetService(postgres, new DenyAllPermissionEvaluator());
        var id = await SeedTargetAsync();
        var caller = CallerAuthorization.ForUser(new ClaimsPrincipal(new ClaimsIdentity("test")));

        var act = () => svc.RetireAsync(id, caller);

        await act.Should().ThrowAsync<AuthorizationException>();
    }

    [Fact]
    public async Task DeleteAsync_throws_for_an_unauthorized_user_caller()
    {
        var svc = new TargetService(postgres, new DenyAllPermissionEvaluator());
        var id = await SeedTargetAsync();
        var caller = CallerAuthorization.ForUser(new ClaimsPrincipal(new ClaimsIdentity("test")));

        var act = () => svc.DeleteAsync(id, caller);

        await act.Should().ThrowAsync<AuthorizationException>();
    }

    // ── Matching surfaces hide retired targets ──────────────────────────────

    [Fact]
    public async Task GetAllAsync_hides_retired_unless_includeRetired()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();
        await svc.RetireAsync(id, CallerAuthorization.System);

        (await svc.GetAllAsync(includeRetired: false)).Should().NotContain(t => t.Id == id);
        (await svc.GetAllAsync(includeRetired: true)).Should().Contain(t => t.Id == id);
        (await svc.GetAllAsync()).Should().Contain(t => t.Id == id,
            "the default (management/display surfaces) still lists retired targets");
    }

    [Fact]
    public async Task GetAllWithEnvironmentsAsync_hides_retired_targets()
    {
        var svc = NewService();
        var id = await SeedTargetAsync();
        await svc.RetireAsync(id, CallerAuthorization.System);

        (await svc.GetAllWithEnvironmentsAsync()).Should().NotContain(t => t.Id == id,
            "the deploy-dialog matching surface must not offer a retired target");
    }

    // ── Seeding helpers ─────────────────────────────────────────────────────

    private async Task<Guid> SeedTargetAsync()
    {
        await using var db = postgres.CreateContext();
        var target = new DeploymentTarget
        {
            SpaceId       = WellKnown.DefaultSpaceId,
            Name          = $"decom-{Guid.NewGuid():N}"[..16],
            Roles         = ["web"],
            TransportMode = TransportMode.Reverse,
            Status        = TargetStatus.Online,
        };
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }

    private async Task<Guid> SeedDeploymentForTargetAsync(Guid targetId)
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId        = WellKnown.DefaultSpaceId,
            Slug           = $"decom-{Guid.NewGuid():N}"[..16],
            Name           = "decom-proj",
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, WellKnown.DefaultSpaceId),
        };
        db.Projects.Add(project);
        var env = new DeploymentEnvironment
        {
            SpaceId = WellKnown.DefaultSpaceId,
            Name    = $"decom-{Guid.NewGuid():N}"[..12],
            Slug    = $"decom-{Guid.NewGuid():N}"[..12],
            SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId                    = WellKnown.DefaultSpaceId,
            ProjectId                  = project.Id,
            Version                    = "1.0",
            ProcessSnapshot            = [],
            VariableSnapshot           = [],
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        var deployment = new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = project.Id, ReleaseId = release.Id,
            EnvironmentId = env.Id, Status = DeploymentStatus.Succeeded,
            CompletedUtc = DateTimeOffset.UtcNow,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment.Id;
    }

    private async Task SeedDeploymentWithAssignmentAsync(Guid targetId)
    {
        await using var db = postgres.CreateContext();
        var deploymentId = await SeedDeploymentForTargetAsync(targetId);
        db.TaskTargetAssignments.Add(new TaskTargetAssignment
        {
            SpaceId = WellKnown.DefaultSpaceId, TaskId = deploymentId, TargetId = targetId,
            AddedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedDeploymentWithStepOutcomeAsync(Guid targetId)
    {
        await using var db = postgres.CreateContext();
        var deploymentId = await SeedDeploymentForTargetAsync(targetId);
        db.TaskStepOutcomes.Add(new TaskStepOutcome
        {
            SpaceId      = WellKnown.DefaultSpaceId,
            TaskId       = deploymentId,
            TargetId     = targetId,
            StepIndex    = 0,
            StepName     = "Deploy",
            Outcome      = StepOutcomeKind.Succeeded,
            AttemptCount = 1,
            IsServerSide = false,
            Required     = true,
            StartedUtc   = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}

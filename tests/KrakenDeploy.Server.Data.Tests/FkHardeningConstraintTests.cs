using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Freezes;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration coverage for the fix-4 FK-hardening wave: the new FKs, the
/// NULLS NOT DISTINCT unique keys, the role-scope CHECK, and the environment
/// reference-cleanup interceptor. Each test isolates itself in its own Space
/// so the shared Postgres container's accumulated state can't interfere.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class FkHardeningConstraintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<Guid> NewSpaceAsync()
    {
        var id = Guid.NewGuid();
        await using var db = postgres.CreateContext();
        db.Spaces.Add(new Space { Id = id, Slug = $"fkh-{id:N}", Name = "FK Hardening Space" });
        await db.SaveChangesAsync();
        return id;
    }

    // ── D7 · teams (space_id, name) NULLS NOT DISTINCT ────────────────────────

    [Fact]
    public async Task Two_system_teams_with_same_name_are_rejected()
    {
        await using var db = postgres.CreateContext();
        db.Teams.Add(new Team { Name = "NNP Duplicate", SpaceId = null });
        await db.SaveChangesAsync();

        db.Teams.Add(new Team { Name = "NNP Duplicate", SpaceId = null });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "NULLS NOT DISTINCT forbids two null-space teams sharing a name");
    }

    [Fact]
    public async Task Same_team_name_is_allowed_in_different_spaces()
    {
        var a = await NewSpaceAsync();
        var b = await NewSpaceAsync();
        await using var db = postgres.CreateContext();
        db.Teams.Add(new Team { Name = "Shared Name", SpaceId = a });
        db.Teams.Add(new Team { Name = "Shared Name", SpaceId = b });
        var act = () => db.SaveChangesAsync();
        await act.Should().NotThrowAsync();
    }

    // ── D7 · task_step_outcomes (task_id, step_index, target_id) NNP ──────────

    [Fact]
    public async Task Two_server_step_outcomes_with_null_target_collide()
    {
        var spaceId = await NewSpaceAsync();
        var taskId = await SeedDeploymentAsync(spaceId);

        await using var db = postgres.CreateContext();
        db.TaskStepOutcomes.Add(NewOutcome(spaceId, taskId, stepIndex: 0, targetId: null));
        await db.SaveChangesAsync();

        db.TaskStepOutcomes.Add(NewOutcome(spaceId, taskId, stepIndex: 0, targetId: null));
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "NULLS NOT DISTINCT collapses the null target so (task, step, null) is unique");
    }

    private static TaskStepOutcome NewOutcome(Guid spaceId, Guid taskId, int stepIndex, Guid? targetId) => new()
    {
        SpaceId = spaceId, TaskId = taskId, StepIndex = stepIndex, TargetId = targetId,
        StepName = "step", Outcome = StepOutcomeKind.Succeeded, AttemptCount = 1,
        CompletedUtc = DateTimeOffset.UtcNow, IsServerSide = true, Required = true,
    };

    // ── D8 · one default channel per project (filtered unique) ────────────────

    [Fact]
    public async Task Two_default_channels_in_one_project_are_rejected()
    {
        var spaceId = await NewSpaceAsync();
        var projectId = await SeedProjectAsync(spaceId);

        await using var db = postgres.CreateContext();
        db.Channels.Add(new Channel { SpaceId = spaceId, ProjectId = projectId, Name = "Default", IsDefault = true });
        await db.SaveChangesAsync();

        db.Channels.Add(new Channel { SpaceId = spaceId, ProjectId = projectId, Name = "Second", IsDefault = true });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the partial unique index allows at most one is_default channel per project");
    }

    // ── D9 · lifecycle delete RESTRICT while referenced ───────────────────────

    [Fact]
    public async Task Deleting_a_lifecycle_referenced_by_a_project_is_blocked()
    {
        var spaceId = await NewSpaceAsync();
        Guid lifecycleId;
        await using (var db = postgres.CreateContext())
        {
            var lc = new Lifecycle { SpaceId = spaceId, Name = "Gated" };
            db.Lifecycles.Add(lc);
            await db.SaveChangesAsync();
            lifecycleId = lc.Id;

            var project = new Project
            {
                SpaceId = spaceId,
                ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
                Name = "Gated Project", Slug = $"gated-{spaceId:N}", LifecycleId = lifecycleId,
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
        }

        // Assert the DB-level RESTRICT (the service adds a friendly guard on top,
        // but the FK is the real enforcement and is Space-context-agnostic).
        await using (var db = postgres.CreateContext())
        {
            var lc = await db.Lifecycles.IgnoreQueryFilters().FirstAsync(l => l.Id == lifecycleId);
            db.Lifecycles.Remove(lc);
            var act = () => db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "projects.lifecycle_id is now RESTRICT, not SET NULL");
        }
    }

    // ── D5 · subscription children cascade ────────────────────────────────────

    [Fact]
    public async Task Deleting_a_subscription_cascades_deliveries_and_outbox()
    {
        Guid subId;
        await using (var db = postgres.CreateContext())
        {
            var sub = new EventSubscription { Name = "Cascade Sub" };
            db.EventSubscriptions.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;

            db.SubscriptionDeliveries.Add(new SubscriptionDelivery
            {
                SubscriptionId = subId, EventId = Guid.NewGuid(),
                Transport = SubscriptionTransport.Webhook, StartedUtc = DateTimeOffset.UtcNow,
                Outcome = SubscriptionDeliveryOutcome.Succeeded,
            });
            db.EmailDigestOutbox.Add(new EmailDigestOutboxEntry
            {
                SubscriptionId = subId, EventId = Guid.NewGuid(), AddedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            db.EventSubscriptions.Remove(await db.EventSubscriptions.FirstAsync(s => s.Id == subId));
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            (await db.SubscriptionDeliveries.CountAsync(d => d.SubscriptionId == subId)).Should().Be(0);
            (await db.EmailDigestOutbox.CountAsync(e => e.SubscriptionId == subId)).Should().Be(0);
        }
    }

    // ── D1 · user-owned rows cascade on user delete ───────────────────────────

    [Fact]
    public async Task Deleting_a_user_cascades_their_api_keys()
    {
        var userId = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId, UserName = $"u{userId:N}", NormalizedUserName = $"U{userId:N}",
                Email = $"{userId:N}@example.test", NormalizedEmail = $"{userId:N}@EXAMPLE.TEST",
                SecurityStamp = Guid.NewGuid().ToString(),
            });
            db.ApiKeys.Add(new ApiKey
            {
                UserId = userId, Name = "key", Prefix = "kd_test", KeyHash = Guid.NewGuid().ToString("N"),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            db.Users.Remove(await db.Users.FirstAsync(u => u.Id == userId));
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            (await db.ApiKeys.CountAsync(k => k.UserId == userId)).Should().Be(0);
        }
    }

    // ── D11 · role_assignment_scopes CHECK + per-dimension cascade ────────────

    [Fact]
    public async Task Scope_row_with_two_dimensions_is_rejected()
    {
        var spaceId = await NewSpaceAsync();
        var assignmentId = await SeedRoleAssignmentAsync(spaceId);

        await using var db = postgres.CreateContext();
        db.RoleAssignmentScopes.Add(new RoleAssignmentScope
        {
            RoleAssignmentId = assignmentId, ProjectId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(),
        });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the CHECK enforces exactly one dimension per scope row");
    }

    [Fact]
    public async Task Scope_row_with_no_dimension_is_rejected()
    {
        var spaceId = await NewSpaceAsync();
        var assignmentId = await SeedRoleAssignmentAsync(spaceId);

        await using var db = postgres.CreateContext();
        db.RoleAssignmentScopes.Add(new RoleAssignmentScope { RoleAssignmentId = assignmentId });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("num_nonnulls(...) = 1 rejects an all-null scope row");
    }

    [Fact]
    public async Task Deleting_the_only_scoped_project_deletes_the_grant_not_widens_it()
    {
        // Escalation guard: a grant scoped ONLY to project X must NOT silently
        // widen to whole-Space when X is deleted (empty scopes = match-all).
        // The cleanup interceptor deletes the now-meaningless assignment instead.
        var spaceId = await NewSpaceAsync();
        var assignmentId = await SeedRoleAssignmentAsync(spaceId);
        var projectId = await SeedProjectAsync(spaceId);

        await using (var db = postgres.CreateContext())
        {
            db.RoleAssignmentScopes.Add(new RoleAssignmentScope
            {
                RoleAssignmentId = assignmentId, ProjectId = projectId,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            db.Projects.Remove(await db.Projects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectId));
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            (await db.RoleAssignments.IgnoreQueryFilters().AnyAsync(a => a.Id == assignmentId))
                .Should().BeFalse("the grant applied only to the deleted project, so it must vanish, not widen");
            (await db.RoleAssignmentScopes.CountAsync(s => s.RoleAssignmentId == assignmentId))
                .Should().Be(0);
        }
    }

    [Fact]
    public async Task Deleting_one_of_several_scoped_projects_only_narrows_the_grant()
    {
        // A multi-scope grant (projects A + B) must survive deleting A, keeping
        // its B scope — narrowed, not emptied, not widened.
        var spaceId = await NewSpaceAsync();
        var assignmentId = await SeedRoleAssignmentAsync(spaceId);
        var projectA = await SeedProjectAsync(spaceId);
        var projectB = await SeedProjectAsync(spaceId);

        await using (var db = postgres.CreateContext())
        {
            db.RoleAssignmentScopes.Add(new RoleAssignmentScope { RoleAssignmentId = assignmentId, ProjectId = projectA });
            db.RoleAssignmentScopes.Add(new RoleAssignmentScope { RoleAssignmentId = assignmentId, ProjectId = projectB });
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            db.Projects.Remove(await db.Projects.IgnoreQueryFilters().FirstAsync(p => p.Id == projectA));
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            (await db.RoleAssignments.IgnoreQueryFilters().AnyAsync(a => a.Id == assignmentId))
                .Should().BeTrue("the grant still applies to project B");
            var remaining = await db.RoleAssignmentScopes.IgnoreQueryFilters()
                .Where(s => s.RoleAssignmentId == assignmentId).ToListAsync();
            remaining.Should().ContainSingle().Which.ProjectId.Should().Be(projectB);
        }
    }

    // ── D12 · environment reference-cleanup interceptor ───────────────────────

    [Fact]
    public async Task Hard_deleting_an_environment_sweeps_it_from_freeze_scope()
    {
        var spaceId = await NewSpaceAsync();
        Guid envId, freezeId;
        await using (var db = postgres.CreateContext())
        {
            var env = new DeploymentEnvironment { SpaceId = spaceId, Name = "Doomed", Slug = $"doomed-{spaceId:N}", SortOrder = 1 };
            db.Environments.Add(env);
            await db.SaveChangesAsync();
            envId = env.Id;

            var freeze = new DeploymentFreeze
            {
                SpaceId = spaceId, Name = "Freeze", EnvironmentIds = [envId, Guid.NewGuid()],
                StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddDays(1),
            };
            db.DeploymentFreezes.Add(freeze);
            await db.SaveChangesAsync();
            freezeId = freeze.Id;
        }

        await using (var db = postgres.CreateContext())
        {
            db.Environments.Remove(await db.Environments.IgnoreQueryFilters().FirstAsync(e => e.Id == envId));
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            var freeze = await db.DeploymentFreezes.IgnoreQueryFilters().FirstAsync(f => f.Id == freezeId);
            freeze.EnvironmentIds.Should().NotContain(envId, "the interceptor sweeps the deleted env id");
        }
    }

    // ── seed helpers ──────────────────────────────────────────────────────────

    private async Task<Guid> SeedProjectAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId = spaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
            Name = "Project", Slug = $"proj-{Guid.NewGuid():N}",
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Guid> SeedRoleAssignmentAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var role = new Role { Name = $"Role {Guid.NewGuid():N}", GrantedPermissions = [] };
        var team = new Team { Name = $"Team {Guid.NewGuid():N}", SpaceId = spaceId };
        db.Roles.Add(role);
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        var assignment = new RoleAssignment { TeamId = team.Id, RoleId = role.Id, SpaceId = spaceId };
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task<Guid> SeedDeploymentAsync(Guid spaceId)
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            SpaceId = spaceId,
            ProjectGroupId = await TestData.EnsureProjectGroupAsync(db, spaceId),
            Name = "Dep Project", Slug = $"dep-{Guid.NewGuid():N}",
        };
        var env = new DeploymentEnvironment { SpaceId = spaceId, Name = "env", Slug = $"env-{Guid.NewGuid():N}", SortOrder = 1 };
        db.Projects.Add(project);
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        var release = new Release
        {
            SpaceId = spaceId, ProjectId = project.Id, Version = "1.0.0",
            ProcessSnapshot = [], VariableSnapshot = [], VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            SpaceId = spaceId, ProjectId = project.Id, ReleaseId = release.Id, EnvironmentId = env.Id,
            Status = DeploymentStatus.Succeeded,
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment.Id;
    }
}

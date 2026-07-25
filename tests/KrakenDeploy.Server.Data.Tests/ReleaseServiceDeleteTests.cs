using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP5 item 2 — release delete. A release deletes cleanly while no deployment
/// references it (its process/variable snapshots are owned JSONB on the row); it
/// is REFUSED while any deployment references it (server_tasks.release_id is a
/// RESTRICT FK — execution history is delete-proof, decision 7).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ReleaseServiceDeleteTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private ReleaseService NewService() =>
        new(postgres, new AllowAllPermissionEvaluator());

    [Fact]
    public async Task DeleteAsync_removes_a_release_with_no_deployments()
    {
        var svc = NewService();
        var projectId = await SeedProjectWithProcessAsync();
        var release = await svc.CreateAsync(projectId, "1.0.0", CallerAuthorization.System);

        var ok = await svc.DeleteAsync(release.Id, CallerAuthorization.System);

        ok.Should().BeTrue();
        await using var db = postgres.CreateContext();
        (await db.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == release.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_is_refused_while_a_deployment_references_the_release()
    {
        var svc = NewService();
        var projectId = await SeedProjectWithProcessAsync();
        var release = await svc.CreateAsync(projectId, "1.0.0", CallerAuthorization.System);
        await SeedDeploymentAsync(projectId, release.Id);

        var act = () => svc.DeleteAsync(release.Id, CallerAuthorization.System);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("deployment");

        await using var db = postgres.CreateContext();
        (await db.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == release.Id))
            .Should().BeTrue("a release with deployment history must be preserved");
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_missing_release()
    {
        var svc = NewService();
        (await svc.DeleteAsync(Guid.NewGuid(), CallerAuthorization.System)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_throws_for_an_unauthorized_user_caller()
    {
        var svc = new ReleaseService(postgres, new DenyAllPermissionEvaluator());
        var projectId = await SeedProjectWithProcessAsync();
        var release = await new ReleaseService(postgres, new AllowAllPermissionEvaluator())
            .CreateAsync(projectId, "1.0.0", CallerAuthorization.System);
        var caller = CallerAuthorization.ForUser(new ClaimsPrincipal(new ClaimsIdentity("test")));

        var act = () => svc.DeleteAsync(release.Id, caller);

        await act.Should().ThrowAsync<AuthorizationException>();
    }

    // ── Seeding helpers ─────────────────────────────────────────────────────

    private async Task<Guid> SeedProjectWithProcessAsync()
    {
        await using var db = postgres.CreateContext();
        var slug = $"reldel-{Guid.NewGuid():N}"[..16];
        var project = new Project
        {
            SpaceId        = WellKnown.DefaultSpaceId,
            Slug           = slug,
            Name           = slug,
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
            Name        = "Deploy",
            StepType    = "Kraken.Script",
            TargetRoles = ["web"],
            Config      = [],
            SortOrder   = 0,
        });
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedDeploymentAsync(Guid projectId, Guid releaseId)
    {
        await using var db = postgres.CreateContext();
        var env = new DeploymentEnvironment
        {
            SpaceId   = WellKnown.DefaultSpaceId,
            Name      = $"reldel-{Guid.NewGuid():N}"[..12],
            Slug      = $"reldel-{Guid.NewGuid():N}"[..12],
            SortOrder = 1,
        };
        db.Environments.Add(env);
        await db.SaveChangesAsync();

        db.Deployments.Add(new Deployment
        {
            SpaceId = WellKnown.DefaultSpaceId, ProjectId = projectId, ReleaseId = releaseId,
            EnvironmentId = env.Id, Status = DeploymentStatus.Succeeded,
            CompletedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}

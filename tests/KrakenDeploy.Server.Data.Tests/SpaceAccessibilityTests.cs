using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Docker/Postgres tests for the accessible-Space gating that backs the hard
/// tenant boundary: <see cref="PermissionEvaluator.GetAccessibleSpaceIdsAsync"/>
/// and the anti-lockout bootstrap in <see cref="SpaceService.CreateAsync"/>.
/// Shares one database per class (no per-test reset) so every fixture uses
/// freshly-generated GUIDs.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SpaceAccessibilityTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "Test"));

    private static Space ActiveSpace() => new()
    {
        Slug   = $"acc-{Guid.NewGuid():N}",
        Name   = $"acc-{Guid.NewGuid():N}",
        Status = SpaceStatus.Active,
    };

    [Fact]
    public async Task Member_reaches_only_their_space_not_others()
    {
        var userId = Guid.NewGuid();
        var spaceX = ActiveSpace();
        var spaceY = ActiveSpace();

        await using (var db = postgres.CreateContext())
        {
            var role = new Role
            {
                Name = $"acc-role-{Guid.NewGuid():N}",
                GrantedPermissions = [Permission.ProjectView],
            };
            var teamX = new Team { Name = $"acc-team-x-{Guid.NewGuid():N}", SpaceId = spaceX.Id };
            db.Spaces.AddRange(spaceX, spaceY);
            db.Roles.Add(role);
            db.Teams.Add(teamX);
            await db.SaveChangesAsync();

            db.RoleAssignments.Add(new RoleAssignment { TeamId = teamX.Id, RoleId = role.Id, SpaceId = spaceX.Id });
            await TestData.EnsureUserAsync(db, userId);
            db.Add(new TeamMember { TeamId = teamX.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var accessible = await evaluator.GetAccessibleSpaceIdsAsync(User(userId));

        accessible.Should().Contain(spaceX.Id, "the user is a real member of a team scoped to Space X");
        accessible.Should().NotContain(spaceY.Id, "the user has no membership reaching Space Y");
    }

    [Fact]
    public async Task System_wide_assignment_pins_no_space()
    {
        var userId = Guid.NewGuid();

        await using (var db = postgres.CreateContext())
        {
            // A system-level team with a non-admin role assigned system-wide
            // (SpaceId == null). This grants a permission everywhere but pins no
            // specific Space, so it must NOT appear as an accessible Space.
            var role = new Role
            {
                Name = $"acc-sysrole-{Guid.NewGuid():N}",
                GrantedPermissions = [Permission.EventViewUnscoped],
            };
            var team = new Team { Name = $"acc-systeam-{Guid.NewGuid():N}", SpaceId = null };
            db.Roles.Add(role);
            db.Teams.Add(team);
            await db.SaveChangesAsync();

            db.RoleAssignments.Add(new RoleAssignment { TeamId = team.Id, RoleId = role.Id, SpaceId = null });
            await TestData.EnsureUserAsync(db, userId);
            db.Add(new TeamMember { TeamId = team.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var accessible = await evaluator.GetAccessibleSpaceIdsAsync(User(userId));

        accessible.Should().BeEmpty("a system-wide grant (SpaceId == null) pins no Space");
    }

    [Fact]
    public async Task System_admin_reaches_every_active_space_but_not_archived()
    {
        var userId = Guid.NewGuid();
        var activeA = ActiveSpace();
        var activeB = ActiveSpace();
        var archived = new Space
        {
            Slug   = $"acc-arch-{Guid.NewGuid():N}",
            Name   = $"acc-arch-{Guid.NewGuid():N}",
            Status = SpaceStatus.Archived,
        };

        await using (var db = postgres.CreateContext())
        {
            var adminRole = new Role
            {
                Name = $"acc-admin-{Guid.NewGuid():N}",
                GrantedPermissions = [Permission.AdministerSystem],
            };
            var adminTeam = new Team { Name = $"acc-adminteam-{Guid.NewGuid():N}", SpaceId = null };
            db.Spaces.AddRange(activeA, activeB, archived);
            db.Roles.Add(adminRole);
            db.Teams.Add(adminTeam);
            await db.SaveChangesAsync();

            db.RoleAssignments.Add(new RoleAssignment { TeamId = adminTeam.Id, RoleId = adminRole.Id, SpaceId = null });
            await TestData.EnsureUserAsync(db, userId);
            db.Add(new TeamMember { TeamId = adminTeam.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var accessible = await evaluator.GetAccessibleSpaceIdsAsync(User(userId));

        accessible.Should().Contain(activeA.Id);
        accessible.Should().Contain(activeB.Id);
        accessible.Should().NotContain(archived.Id, "archived Spaces are not accessible even to system admins");
    }

    [Fact]
    public async Task CreateAsync_makes_creator_a_space_manager()
    {
        var creatorId = Guid.NewGuid();

        // Roles must exist so the seeded Space-Managers assignment resolves to a
        // role carrying SpaceEdit.
        await new BuiltInRbacSeeder(postgres, NullLogger<BuiltInRbacSeeder>.Instance).SeedAsync();

        // team_members now FKs to users (fix 4 decision 1); CreateAsync adds the
        // creator as a member, so the creator user must exist.
        await using (var seed = postgres.CreateContext())
        {
            await TestData.EnsureUserAsync(seed, creatorId);
        }

        var spaceSvc = new SpaceService(postgres);
        var space = await spaceSvc.CreateAsync(
            $"created-{Guid.NewGuid():N}", "Created Space", null, creatorId);

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);

        var accessible = await evaluator.GetAccessibleSpaceIdsAsync(User(creatorId));
        accessible.Should().Contain(space.Id, "the creator was added to the new Space's Space Managers team");

        var canEdit = await evaluator.HasPermissionAsync(
            User(creatorId), Permission.SpaceEdit, new PermissionScope(SpaceId: space.Id));
        canEdit.Should().BeTrue("Space Manager grants SpaceEdit within the Space");

        // And membership is exactly via the deterministic Space-Managers team.
        await using var db = postgres.CreateContext();
        var managersTeamId = BuiltInRbacSeeder.SpaceManagersTeamId(space.Id);
        (await db.TeamMembers.AnyAsync(m => m.TeamId == managersTeamId && m.UserId == creatorId))
            .Should().BeTrue();
    }
}

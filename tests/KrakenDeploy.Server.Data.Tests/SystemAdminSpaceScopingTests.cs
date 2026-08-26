using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP3-c (c) — the system-admin short-circuit honours the role assignment's own
/// <see cref="RoleAssignment.SpaceId"/>. Before the fix,
/// <c>PermissionEvaluator.UserIsSystemAdminAsync</c> filtered assignments only by
/// team, so <see cref="Permission.AdministerSystem"/> granted for ONE Space was
/// god mode in EVERY Space (and system-wide). After the fix a Space-pinned
/// AdministerSystem is god mode only inside its Space; global god mode requires
/// a system-scope (SpaceId == null) assignment — the only shape the built-in
/// seeder ever creates.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SystemAdminSpaceScopingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "Test"));

    private static Space ActiveSpace() => new()
    {
        Slug   = $"sysadm-{Guid.NewGuid():N}",
        Name   = $"sysadm-{Guid.NewGuid():N}",
        Status = SpaceStatus.Active,
    };

    /// <summary>Seeds a user on a team holding AdministerSystem via an assignment
    /// pinned to <paramref name="assignmentSpaceId"/> (null = system-scope).</summary>
    private async Task<Guid> SeedAdminGrantAsync(Guid? assignmentSpaceId)
    {
        var userId = Guid.NewGuid();
        await using var db = postgres.CreateContext();
        var role = new Role
        {
            Name = $"sysadm-role-{Guid.NewGuid():N}",
            GrantedPermissions = [Permission.AdministerSystem],
        };
        var team = new Team { Name = $"sysadm-team-{Guid.NewGuid():N}", SpaceId = assignmentSpaceId };
        db.Roles.Add(role);
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        db.RoleAssignments.Add(new RoleAssignment
        {
            TeamId  = team.Id,
            RoleId  = role.Id,
            SpaceId = assignmentSpaceId,
        });
        await TestData.EnsureUserAsync(db, userId);
        db.Add(new TeamMember { TeamId = team.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return userId;
    }

    [Fact]
    public async Task Space_pinned_admin_is_god_mode_only_inside_its_space()
    {
        var spaceA = ActiveSpace();
        var spaceB = ActiveSpace();
        await using (var db = postgres.CreateContext())
        {
            db.Spaces.AddRange(spaceA, spaceB);
            await db.SaveChangesAsync();
        }
        var userId = await SeedAdminGrantAsync(spaceA.Id);

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var user = User(userId);

        // Inside the pinned Space the short-circuit still applies — an arbitrary
        // permission the role does not name explicitly.
        (await evaluator.HasPermissionAsync(user, Permission.ProjectDelete, new PermissionScope(SpaceId: spaceA.Id)))
            .Should().BeTrue("AdministerSystem pinned to Space A is still god mode inside Space A");

        // Deliberately AFTER the Space-A check on the SAME evaluator instance:
        // with the pre-fix per-user cache (a plain bool keyed by user id), the
        // Space-A answer would be served here and this would return true.
        (await evaluator.HasPermissionAsync(user, Permission.ProjectDelete, new PermissionScope(SpaceId: spaceB.Id)))
            .Should().BeFalse("AdministerSystem pinned to Space A must not reach Space B");
        (await evaluator.HasPermissionAsync(user, Permission.AdministerSystem, new PermissionScope(SpaceId: spaceB.Id)))
            .Should().BeFalse("even the permission itself does not apply outside the pinned Space");

        // System-wide questions (no Space in scope): only a system-scope
        // assignment may answer them.
        (await evaluator.HasPermissionAsync(user, Permission.AdministerSystem, new PermissionScope()))
            .Should().BeFalse("a Space-pinned grant must not pass a system-wide check");
    }

    [Fact]
    public async Task System_scope_admin_remains_god_mode_everywhere()
    {
        var spaceA = ActiveSpace();
        var spaceB = ActiveSpace();
        await using (var db = postgres.CreateContext())
        {
            db.Spaces.AddRange(spaceA, spaceB);
            await db.SaveChangesAsync();
        }
        var userId = await SeedAdminGrantAsync(assignmentSpaceId: null);

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var user = User(userId);

        (await evaluator.HasPermissionAsync(user, Permission.ProjectDelete, new PermissionScope(SpaceId: spaceA.Id)))
            .Should().BeTrue("a system-scope AdministerSystem assignment is god mode in every Space");
        (await evaluator.HasPermissionAsync(user, Permission.ProjectDelete, new PermissionScope(SpaceId: spaceB.Id)))
            .Should().BeTrue("a system-scope AdministerSystem assignment is god mode in every Space");
        (await evaluator.HasPermissionAsync(user, Permission.AdministerSystem, new PermissionScope()))
            .Should().BeTrue("system-wide checks are exactly what the system-scope assignment answers");

        var accessible = await evaluator.GetAccessibleSpaceIdsAsync(user);
        accessible.Should().Contain([spaceA.Id, spaceB.Id],
            "a system-scope admin reaches every Active Space");
    }

    [Fact]
    public async Task Space_pinned_admin_permission_set_is_full_only_in_its_space()
    {
        var spaceA = ActiveSpace();
        var spaceB = ActiveSpace();
        await using (var db = postgres.CreateContext())
        {
            db.Spaces.AddRange(spaceA, spaceB);
            await db.SaveChangesAsync();
        }
        var userId = await SeedAdminGrantAsync(spaceA.Id);

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var user = User(userId);

        var inA = await evaluator.GetPermissionsAsync(user, new PermissionScope(SpaceId: spaceA.Id));
        inA.Should().BeEquivalentTo(Enum.GetValues<Permission>(),
            "inside the pinned Space the admin gets the full permission union");

        var inB = await evaluator.GetPermissionsAsync(user, new PermissionScope(SpaceId: spaceB.Id));
        inB.Should().NotContain(Permission.AdministerSystem,
            "outside the pinned Space the grant contributes nothing");
    }

    [Fact]
    public async Task Space_pinned_admin_reaches_only_its_own_space()
    {
        var spaceA = ActiveSpace();
        var spaceB = ActiveSpace();
        await using (var db = postgres.CreateContext())
        {
            db.Spaces.AddRange(spaceA, spaceB);
            await db.SaveChangesAsync();
        }
        var userId = await SeedAdminGrantAsync(spaceA.Id);

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var accessible = await evaluator.GetAccessibleSpaceIdsAsync(User(userId));

        accessible.Should().Contain(spaceA.Id,
            "the pinned assignment reaches its own Space through the ordinary membership sweep");
        accessible.Should().NotContain(spaceB.Id,
            "a Space-pinned AdministerSystem must not open every other Space");
    }
}

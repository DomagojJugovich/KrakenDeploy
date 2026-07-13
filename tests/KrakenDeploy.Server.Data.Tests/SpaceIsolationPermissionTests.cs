using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Regression test for the Space-isolation leak: a per-Space "Everyone" team
/// (IsEveryoneTeam=true, with a Space-scoped grant) was added to EVERY user by
/// GetUserTeamIdsAsync with no Space/membership filter, so every authenticated
/// user got that team's grant on EVERY Space. After the fix a per-Space Everyone
/// team applies only to users who are real members of that Space; the system-level
/// Everyone team (SpaceId==null) still applies to everyone.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class SpaceIsolationPermissionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "Test"));

    [Fact]
    public async Task PerSpace_everyone_grant_reaches_only_members_of_that_space()
    {
        var userId = Guid.NewGuid();
        var spaceA = Guid.NewGuid();   // user is a member here
        var spaceB = Guid.NewGuid();   // user is NOT a member here

        await using (var db = postgres.CreateContext())
        {
            var viewerRole = new Role
            {
                Name = $"iso-viewer-{Guid.NewGuid():N}",
                GrantedPermissions = [Permission.PackageView],
            };
            // Per-Space "Everyone" teams in A and B, each granting the marker
            // permission Space-wide — the exact shape BuiltInRbacSeeder creates.
            var everyoneA = new Team { Name = $"iso-everyone-a-{Guid.NewGuid():N}", SpaceId = spaceA, IsEveryoneTeam = true };
            var everyoneB = new Team { Name = $"iso-everyone-b-{Guid.NewGuid():N}", SpaceId = spaceB, IsEveryoneTeam = true };
            // A concrete Space-A team the user is an EXPLICIT member of.
            var membersA = new Team { Name = $"iso-members-a-{Guid.NewGuid():N}", SpaceId = spaceA };
            db.Roles.Add(viewerRole);
            db.Teams.AddRange(everyoneA, everyoneB, membersA);
            await db.SaveChangesAsync();

            db.RoleAssignments.Add(new RoleAssignment { TeamId = everyoneA.Id, RoleId = viewerRole.Id, SpaceId = spaceA });
            db.RoleAssignments.Add(new RoleAssignment { TeamId = everyoneB.Id, RoleId = viewerRole.Id, SpaceId = spaceB });
            await TestData.EnsureUserAsync(db, userId);
            db.Add(new TeamMember { TeamId = membersA.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var evaluator = new PermissionEvaluator(postgres, TimeProvider.System);
        var user = User(userId);

        (await evaluator.HasPermissionAsync(user, Permission.PackageView, new PermissionScope(SpaceId: spaceA)))
            .Should().BeTrue("the user is a real member of Space A, so A's Everyone-team grant applies");
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, new PermissionScope(SpaceId: spaceB)))
            .Should().BeFalse("the user is NOT a member of Space B — its Everyone team must no longer be virtual-for-all");
    }
}

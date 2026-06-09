using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Verifies the absolute TTL on <see cref="PermissionEvaluator"/>'s
/// per-(user, space) caches. In Blazor Server a DI scope = the whole circuit,
/// so without a TTL a revoked role would only take effect on reconnect. The TTL
/// bounds that staleness to seconds; <c>bypassCache: true</c> (used by the
/// execution-time action guard) reflects the change immediately.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class PermissionEvaluatorCacheTtlTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "Test"));

    [Fact]
    public async Task Revoked_permission_is_served_stale_within_TTL_then_refetched()
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var scope = new PermissionScope(SpaceId: spaceId);

        var assignmentId = await SeedGrantAsync(userId, spaceId, Permission.PackageView);

        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var evaluator = new PermissionEvaluator(postgres, clock);
        var user = User(userId);

        // Granted → true, and now cached for this (user, space).
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, scope))
            .Should().BeTrue();

        // Revoke the assignment out-of-band (a separate context).
        await RemoveAssignmentAsync(assignmentId);

        // Still within the TTL (no clock advance) → served stale from cache.
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, scope))
            .Should().BeTrue("the cached entry is still within its TTL");

        // Advance past the TTL → the next check refetches and sees the revocation.
        clock.Advance(TimeSpan.FromSeconds(61));
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, scope))
            .Should().BeFalse("once the TTL elapses the entry is refetched from the DB");
    }

    [Fact]
    public async Task BypassCache_reflects_a_revocation_immediately()
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var scope = new PermissionScope(SpaceId: spaceId);

        var assignmentId = await SeedGrantAsync(userId, spaceId, Permission.PackageView);

        // Frozen clock: nothing below relies on time passing — bypassCache must
        // not depend on the TTL elapsing.
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var evaluator = new PermissionEvaluator(postgres, clock);
        var user = User(userId);

        // Warm the cache as "granted".
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, scope))
            .Should().BeTrue();

        await RemoveAssignmentAsync(assignmentId);

        // Cached (default) path is still stale...
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, scope))
            .Should().BeTrue();

        // ...but the authoritative path must not be.
        (await evaluator.HasPermissionAsync(user, Permission.PackageView, scope, bypassCache: true))
            .Should().BeFalse("an execution-time guard must never authorize on stale cache");
    }

    // ── Seeding helpers ───────────────────────────────────────────────────────

    private async Task<Guid> SeedGrantAsync(Guid userId, Guid spaceId, Permission permission)
    {
        await using var db = postgres.CreateContext();

        var role = new Role
        {
            Name = $"ttl-role-{Guid.NewGuid():N}",
            GrantedPermissions = [permission],
        };
        var team = new Team { Name = $"ttl-team-{Guid.NewGuid():N}", SpaceId = spaceId };
        db.Roles.Add(role);
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        db.Add(new TeamMember { TeamId = team.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
        var assignment = new RoleAssignment { TeamId = team.Id, RoleId = role.Id, SpaceId = spaceId };
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync();

        return assignment.Id;
    }

    private async Task RemoveAssignmentAsync(Guid assignmentId)
    {
        await using var db = postgres.CreateContext();
        await db.RoleAssignments.Where(a => a.Id == assignmentId).ExecuteDeleteAsync();
    }

    /// <summary>Minimal controllable clock — the evaluator only reads GetUtcNow().</summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

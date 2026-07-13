using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// The M13.C.4 cage: a principal carrying <see cref="KrakenClaimTypes.ApiKeySpace"/>
/// (a Space-restricted API key) must be denied on every permission check whose
/// scope falls outside that Space — including system-wide checks and including
/// owners who are system administrators. The claim is stamped only by the
/// API-key auth handler, so cookie principals are unaffected.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ApiKeySpaceRestrictionTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    // A pure API-key request: single identity authenticated via the ApiKey
    // scheme (matches ApiKeyAuthenticationHandler's `new ClaimsIdentity(claims,
    // Scheme.Name)`), carrying the Space restriction.
    private static ClaimsPrincipal RestrictedUser(Guid userId, Guid restrictedSpace) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(KrakenClaimTypes.ApiKeySpace, restrictedSpace.ToString()),
        ], authenticationType: KrakenAuthSchemes.ApiKey));

    private static ClaimsPrincipal PlainUser(Guid userId) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], authenticationType: "Test"));

    /// <summary>
    /// Reproduces what ASP.NET Core's IPolicyEvaluator actually builds when a
    /// cookie request ALSO carries a restricted X-Api-Key: two identities
    /// merged into one principal — the cookie identity (NOT the ApiKey scheme)
    /// plus an ApiKey-scheme identity carrying the space claim.
    /// </summary>
    private static ClaimsPrincipal MergedCookiePlusApiKey(Guid userId, Guid apiKeySpace)
    {
        var cookie = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "Identity.Application");
        var apiKey = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(KrakenClaimTypes.ApiKeySpace, apiKeySpace.ToString()),
            ],
            authenticationType: KrakenAuthSchemes.ApiKey);
        return new ClaimsPrincipal([cookie, apiKey]);
    }

    private PermissionEvaluator BuildEvaluator() => new(postgres, TimeProvider.System);

    [Fact]
    public async Task Restricted_key_allows_inside_and_denies_outside_its_space()
    {
        var userId = Guid.NewGuid();
        var homeSpace = Guid.NewGuid();
        var otherSpace = Guid.NewGuid();
        await SeedGrantAsync(userId, homeSpace, Permission.PackageView);
        await SeedGrantAsync(userId, otherSpace, Permission.PackageView);

        var evaluator = BuildEvaluator();
        var restricted = RestrictedUser(userId, homeSpace);

        (await evaluator.HasPermissionAsync(
                restricted, Permission.PackageView, new PermissionScope(SpaceId: homeSpace)))
            .Should().BeTrue("inside the bound Space the owner's real grants apply");

        (await evaluator.HasPermissionAsync(
                restricted, Permission.PackageView, new PermissionScope(SpaceId: otherSpace)))
            .Should().BeFalse(
                "the owner HAS the grant in the other Space, but the key is caged — " +
                "that asymmetry is the entire point of the restriction");

        (await evaluator.HasPermissionAsync(restricted, Permission.PackageView))
            .Should().BeFalse("a system-wide scope (null SpaceId) is outside any single Space");

        // Control: the same owner through a cookie (no claim) is NOT caged.
        (await evaluator.HasPermissionAsync(
                PlainUser(userId), Permission.PackageView, new PermissionScope(SpaceId: otherSpace)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Merged_cookie_plus_stray_ApiKey_is_NOT_caged_but_pure_ApiKey_is()
    {
        // The finding: IPolicyEvaluator merges every succeeding scheme's
        // identity into HttpContext.User, so a cookie session that also carries
        // a valid restricted X-Api-Key ends up with the space claim on the
        // merged principal. The browser session must stay authoritative.
        var userId = Guid.NewGuid();
        var homeSpace = Guid.NewGuid();
        var otherSpace = Guid.NewGuid();
        await SeedGrantAsync(userId, otherSpace, Permission.PackageView);

        var evaluator = BuildEvaluator();

        // Merged (cookie primary + stray ApiKey identity) → cage MUST NOT apply:
        // the cookie user keeps their real grant in otherSpace.
        var merged = MergedCookiePlusApiKey(userId, apiKeySpace: homeSpace);
        (await evaluator.HasPermissionAsync(
                merged, Permission.PackageView, new PermissionScope(SpaceId: otherSpace)))
            .Should().BeTrue("a stray API-key header must not silently cage a cookie session");

        // Pure API-key request restricted to homeSpace → cage DOES apply.
        var pure = RestrictedUser(userId, homeSpace);
        (await evaluator.HasPermissionAsync(
                pure, Permission.PackageView, new PermissionScope(SpaceId: otherSpace)))
            .Should().BeFalse("a genuine API-key request is caged to its bound Space");
    }

    [Fact]
    public async Task Restricted_key_cages_even_a_system_administrator_owner()
    {
        var adminId = Guid.NewGuid();
        var homeSpace = Guid.NewGuid();
        var otherSpace = Guid.NewGuid();
        await SeedGrantAsync(adminId, homeSpace, Permission.AdministerSystem);

        var evaluator = BuildEvaluator();
        var restricted = RestrictedUser(adminId, homeSpace);

        (await evaluator.HasPermissionAsync(
                restricted, Permission.PackageView, new PermissionScope(SpaceId: otherSpace)))
            .Should().BeFalse(
                "the AdministerSystem short-circuit must run AFTER the cage, or an " +
                "admin-owned CI key would silently be a skeleton key");

        (await evaluator.HasPermissionAsync(
                restricted, Permission.PackageView, new PermissionScope(SpaceId: homeSpace)))
            .Should().BeTrue("inside the bound Space god mode still applies");
    }

    [Fact]
    public async Task GetPermissions_is_empty_outside_the_bound_space()
    {
        var userId = Guid.NewGuid();
        var homeSpace = Guid.NewGuid();
        await SeedGrantAsync(userId, homeSpace, Permission.PackageView);

        var evaluator = BuildEvaluator();
        var restricted = RestrictedUser(userId, homeSpace);

        (await evaluator.GetPermissionsAsync(restricted, new PermissionScope(SpaceId: homeSpace)))
            .Should().Contain(Permission.PackageView);

        (await evaluator.GetPermissionsAsync(restricted, new PermissionScope(SpaceId: Guid.NewGuid())))
            .Should().BeEmpty();

        (await evaluator.GetPermissionsAsync(restricted))
            .Should().BeEmpty("system-wide enumeration must not leak caged permissions");
    }

    [Fact]
    public async Task Accessible_spaces_collapse_to_the_bound_space_for_member_and_admin()
    {
        var userId = Guid.NewGuid();
        var homeSpace = await SeedActiveSpaceAsync();
        var otherSpace = await SeedActiveSpaceAsync();
        await SeedGrantAsync(userId, homeSpace, Permission.PackageView);
        await SeedGrantAsync(userId, otherSpace, Permission.PackageView);

        var evaluator = BuildEvaluator();

        (await evaluator.GetAccessibleSpaceIdsAsync(PlainUser(userId)))
            .Should().Contain([homeSpace, otherSpace], "control: the owner reaches both");

        (await evaluator.GetAccessibleSpaceIdsAsync(RestrictedUser(userId, homeSpace)))
            .Should().BeEquivalentTo([homeSpace]);

        // Admin path: AdministerSystem reaches every Active Space — the cage
        // must cap that early return too.
        var adminId = Guid.NewGuid();
        await SeedGrantAsync(adminId, homeSpace, Permission.AdministerSystem);
        (await evaluator.GetAccessibleSpaceIdsAsync(RestrictedUser(adminId, homeSpace)))
            .Should().BeEquivalentTo([homeSpace]);
    }

    // ── Seeding (mirrors PermissionEvaluatorCacheTtlTests) ────────────────────

    private async Task SeedGrantAsync(Guid userId, Guid spaceId, Permission permission)
    {
        await using var db = postgres.CreateContext();

        var role = new Role
        {
            Name = $"cage-role-{Guid.NewGuid():N}",
            GrantedPermissions = [permission],
        };
        var team = new Team { Name = $"cage-team-{Guid.NewGuid():N}", SpaceId = spaceId };
        db.Roles.Add(role);
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        await TestData.EnsureUserAsync(db, userId);
        db.Add(new TeamMember { TeamId = team.Id, UserId = userId, AddedUtc = DateTimeOffset.UtcNow });
        db.RoleAssignments.Add(new RoleAssignment { TeamId = team.Id, RoleId = role.Id, SpaceId = spaceId });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedActiveSpaceAsync()
    {
        await using var db = postgres.CreateContext();
        var space = new Space { Slug = $"cage-{Guid.NewGuid():N}", Name = "Cage test" };
        db.Spaces.Add(space);
        await db.SaveChangesAsync();
        return space.Id;
    }
}

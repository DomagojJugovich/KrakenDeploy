using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// EF Core-backed <see cref="IPermissionEvaluator"/>. Resolves the user's
/// teams (explicit + external-group + Everyone), pulls their
/// <see cref="RoleAssignment"/>s, and evaluates the requested permission
/// against the scope.
/// <para>
/// Caches per-request keyed by (UserId, SpaceId): the same scope-Space
/// permission set is computed once per request even if dozens of UI elements
/// call <see cref="HasPermissionAsync"/> while rendering.
/// </para>
/// </summary>
public sealed class PermissionEvaluator(KrakenDbContext db) : IPermissionEvaluator
{
    /// <summary>Standard claim name for the user's KrakenDeploy user id.</summary>
    public const string UserIdClaim = ClaimTypes.NameIdentifier;

    private readonly Dictionary<CacheKey, IReadOnlySet<Permission>> _cache = [];

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        Permission permission,
        PermissionScope scope = default,
        CancellationToken ct = default)
    {
        // AdministerSystem is god mode — short-circuits every check, regardless
        // of scope. Granted by being on the "Kraken Administrators" team or
        // any team with a system-wide RoleAssignment to System Administrator.
        if (await UserIsSystemAdminAsync(user, ct).ConfigureAwait(false))
        {
            return true;
        }

        var perms = await GetPermissionsAsync(user, scope, ct).ConfigureAwait(false);
        return perms.Contains(permission);
    }

    public async Task<IReadOnlySet<Permission>> GetPermissionsAsync(
        ClaimsPrincipal user,
        PermissionScope scope = default,
        CancellationToken ct = default)
    {
        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return new HashSet<Permission>();
        }

        var key = new CacheKey(userId.Value, scope.SpaceId);
        if (_cache.TryGetValue(key, out var cached))
        {
            return FilterToScope(cached, scope);
        }

        var allInSpace = await ComputeSpacePermissionsAsync(
            userId.Value, scope.SpaceId, ct).ConfigureAwait(false);

        _cache[key] = allInSpace;
        return FilterToScope(allInSpace, scope);
    }

    // ── Computation ───────────────────────────────────────────────────────────

    private async Task<IReadOnlySet<Permission>> ComputeSpacePermissionsAsync(
        Guid userId, Guid? spaceId, CancellationToken ct)
    {
        // 1. Resolve every team the user belongs to (explicit + external + Everyone).
        var teamIds = await GetUserTeamIdsAsync(userId, ct).ConfigureAwait(false);
        if (teamIds.Count == 0)
        {
            return new HashSet<Permission>();
        }

        // 2. Pull every role assignment for those teams matching the requested
        //    Space (or system-wide assignments which apply to all Spaces).
        var assignments = await db.RoleAssignments
            .IgnoreQueryFilters() // RoleAssignment isn't ISpaceScoped, but be explicit
            .Include(a => a.Role)
            .Where(a => teamIds.Contains(a.TeamId))
            .Where(a => a.SpaceId == null || a.SpaceId == spaceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // 3. The "all permissions in this Space" set is every permission from
        //    every assignment whose dimensions don't exclude the Space-wide
        //    case. Per-entity scope checks happen later in FilterToScope.
        var perms = new HashSet<Permission>();
        foreach (var assignment in assignments)
        {
            foreach (var perm in assignment.Role.GrantedPermissions)
            {
                perms.Add(perm);
            }
        }
        return perms;
    }

    /// <summary>
    /// Filters a Space-wide permission set down to those granted by at least
    /// one role assignment whose scope dimensions match the requested entity
    /// scope. The Space-wide cache key already handles SpaceId; this pass
    /// handles Project / Environment / Tenant restrictions.
    /// </summary>
    private static IReadOnlySet<Permission> FilterToScope(
        IReadOnlySet<Permission> spaceWidePerms,
        PermissionScope scope)
    {
        // When the caller doesn't restrict to a specific Project/Env/Tenant,
        // any permission granted in the Space is sufficient.
        if (scope.ProjectGroupId is null &&
            scope.ProjectId is null &&
            scope.EnvironmentId is null &&
            scope.TenantId is null &&
            scope.TenantTagId is null)
        {
            return spaceWidePerms;
        }

        // TODO(M10/B3): full per-assignment scope evaluation. The current
        // implementation grants any Space-wide permission to any sub-scope —
        // adequate for path-1 (most Role Assignments are unscoped within a
        // Space) but intentionally too permissive for fine-grained scope
        // restrictions. Tightened in a follow-up commit alongside the
        // authorization integration tests.
        return spaceWidePerms;
    }

    // ── Team membership resolution ────────────────────────────────────────────

    private async Task<HashSet<Guid>> GetUserTeamIdsAsync(Guid userId, CancellationToken ct)
    {
        // a. Explicit team_members rows
        var explicitTeams = await db.TeamMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.TeamId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var teamIds = new HashSet<Guid>(explicitTeams);

        // b. The system-level "Everyone" team (every authenticated user is a
        //    virtual member). Per-Space "Everyone" teams matter too — they're
        //    flagged with IsEveryoneTeam = true.
        var everyoneTeams = await db.Teams
            .Where(t => t.IsEveryoneTeam)
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var t in everyoneTeams)
        {
            teamIds.Add(t);
        }

        // c. External-group matches via TeamExternalGroup. Resolved at sign-in
        //    time and cached on the principal as additional team-id claims —
        //    we read those claims rather than re-querying the IdP. (To be
        //    wired up in M10/C with the OIDC integration; for now the
        //    explicit + Everyone path covers the common case.)

        return teamIds;
    }

    // ── System admin shortcut ─────────────────────────────────────────────────

    private async Task<bool> UserIsSystemAdminAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return false;
        }

        // Any role assignment that grants AdministerSystem makes the user a
        // system admin. No scope check — AdministerSystem is system-only.
        return await db.RoleAssignments
            .IgnoreQueryFilters()
            .Where(a => db.TeamMembers.Any(m => m.UserId == userId && m.TeamId == a.TeamId)
                        || db.Teams.Any(t => t.Id == a.TeamId && t.IsEveryoneTeam))
            .AnyAsync(a => a.Role.GrantedPermissions.Contains(Permission.AdministerSystem), ct)
            .ConfigureAwait(false);
    }

    private static Guid? TryGetUserId(ClaimsPrincipal user)
    {
        var idClaim = user.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(idClaim, out var id) ? id : null;
    }

    private readonly record struct CacheKey(Guid UserId, Guid? SpaceId);
}

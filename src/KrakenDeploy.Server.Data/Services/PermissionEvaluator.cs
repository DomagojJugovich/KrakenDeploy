using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// EF Core-backed <see cref="IPermissionEvaluator"/>. Resolves the user's
/// teams (explicit + external-group + Everyone), pulls their
/// <see cref="RoleAssignment"/>s, and evaluates the requested permission
/// against the scope via <see cref="RoleAssignmentScopeMatcher"/>.
/// <para>
/// Caches assignments per request keyed by (UserId, SpaceId): repeated
/// permission checks during one render hit the DB at most once per unique
/// SpaceId. The cached value is the raw assignment list — scope filtering
/// happens per-call so the same cache entry serves many different
/// per-Project / per-Environment / per-Tenant queries.
/// </para>
/// </summary>
public sealed class PermissionEvaluator(KrakenDbContext db) : IPermissionEvaluator
{
    /// <summary>Standard claim name for the user's KrakenDeploy user id.</summary>
    public const string UserIdClaim = ClaimTypes.NameIdentifier;

    private readonly Dictionary<CacheKey, IReadOnlyList<RoleAssignment>> _assignmentCache = [];
    private readonly Dictionary<Guid, bool> _systemAdminCache = [];

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        Permission permission,
        PermissionScope scope = default,
        CancellationToken ct = default)
    {
        // AdministerSystem is god mode — short-circuits every check, regardless
        // of scope. Granted by being on a team whose role assignments include
        // that permission.
        if (await UserIsSystemAdminAsync(user, ct).ConfigureAwait(false))
        {
            return true;
        }

        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return false;
        }

        var assignments = await GetCachedAssignmentsAsync(userId.Value, scope.SpaceId, ct)
            .ConfigureAwait(false);

        // Short-circuit: stop at the first matching assignment that grants
        // the requested permission. Avoids walking the full set when one
        // hit is enough.
        foreach (var a in assignments)
        {
            if (a.Role.GrantedPermissions.Contains(permission)
                && RoleAssignmentScopeMatcher.Matches(a, scope))
            {
                return true;
            }
        }
        return false;
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

        // System admin: union of every defined permission. Cheap because
        // Permission is a bounded enum.
        if (await UserIsSystemAdminAsync(user, ct).ConfigureAwait(false))
        {
            return new HashSet<Permission>(Enum.GetValues<Permission>());
        }

        var assignments = await GetCachedAssignmentsAsync(userId.Value, scope.SpaceId, ct)
            .ConfigureAwait(false);

        var perms = new HashSet<Permission>();
        foreach (var a in assignments)
        {
            if (!RoleAssignmentScopeMatcher.Matches(a, scope))
            {
                continue;
            }

            foreach (var p in a.Role.GrantedPermissions)
            {
                perms.Add(p);
            }
        }
        return perms;
    }

    // ── Cached assignment fetch ───────────────────────────────────────────────

    private async Task<IReadOnlyList<RoleAssignment>> GetCachedAssignmentsAsync(
        Guid userId, Guid? spaceId, CancellationToken ct)
    {
        var key = new CacheKey(userId, spaceId);
        if (_assignmentCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var teamIds = await GetUserTeamIdsAsync(userId, ct).ConfigureAwait(false);
        if (teamIds.Count == 0)
        {
            _assignmentCache[key] = [];
            return [];
        }

        // Pull every role assignment for those teams matching the requested
        // Space (or system-wide assignments which apply to all Spaces).
        // We deliberately keep the raw assignments — scope-dimension filtering
        // is per-call, not per-fetch, so the same cache entry serves many
        // different scoped queries during one render.
        var assignments = await db.RoleAssignments
            .IgnoreQueryFilters() // RoleAssignment isn't ISpaceScoped, but be explicit
            .Include(a => a.Role)
            .Where(a => teamIds.Contains(a.TeamId))
            .Where(a => a.SpaceId == null || a.SpaceId == spaceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _assignmentCache[key] = assignments;
        return assignments;
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
        //    wired up alongside the OIDC integration in M10/F; for now the
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

        if (_systemAdminCache.TryGetValue(userId.Value, out var cached))
        {
            return cached;
        }

        // Any role assignment that grants AdministerSystem makes the user a
        // system admin. No scope check — AdministerSystem is system-only.
        var isAdmin = await db.RoleAssignments
            .IgnoreQueryFilters()
            .Where(a => db.TeamMembers.Any(m => m.UserId == userId && m.TeamId == a.TeamId)
                        || db.Teams.Any(t => t.Id == a.TeamId && t.IsEveryoneTeam))
            .AnyAsync(a => a.Role.GrantedPermissions.Contains(Permission.AdministerSystem), ct)
            .ConfigureAwait(false);

        _systemAdminCache[userId.Value] = isAdmin;
        return isAdmin;
    }

    private static Guid? TryGetUserId(ClaimsPrincipal user)
    {
        var idClaim = user.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(idClaim, out var id) ? id : null;
    }

    private readonly record struct CacheKey(Guid UserId, Guid? SpaceId);
}

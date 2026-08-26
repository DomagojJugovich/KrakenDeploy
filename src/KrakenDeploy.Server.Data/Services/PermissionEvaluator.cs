using System.Collections.Concurrent;
using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// EF Core-backed <see cref="IPermissionEvaluator"/>. Resolves the user's
/// teams (explicit + external-group + Everyone), pulls their
/// <see cref="RoleAssignment"/>s, and evaluates the requested permission
/// against the scope via <see cref="RoleAssignmentScopeMatcher"/>.
/// <para>
/// Caches assignments keyed by (UserId, SpaceId) with a short absolute TTL
/// (<see cref="CacheTtl"/>): repeated permission checks during one render hit
/// the DB at most once per unique SpaceId, while the TTL bounds staleness in a
/// long-lived Blazor circuit so a revoked role takes effect within seconds
/// rather than only at reconnect. The cached value is the raw assignment list —
/// scope filtering happens per-call so the same cache entry serves many
/// different per-Project / per-Environment / per-Tenant queries. Execution-time
/// authorization (the UI action guard) passes <c>bypassCache: true</c> for an
/// authoritative, never-stale read.
/// </para>
/// </summary>
public sealed class PermissionEvaluator(
    IDbContextFactory<KrakenDbContext> dbFactory,
    TimeProvider timeProvider) : IPermissionEvaluator
{
    /// <summary>Standard claim name for the user's KrakenDeploy user id.</summary>
    public const string UserIdClaim = ClaimTypes.NameIdentifier;

    // Absolute TTL bounding how long a cached entry may serve a permission
    // decision. In Blazor Server a DI scope = the whole circuit (long-lived),
    // so without a TTL these caches would serve stale RBAC for the life of the
    // connection — a revoked role would only take effect on reconnect. 60s is
    // the chosen staleness tolerance for the read-only UI gate; execution-time
    // checks pass bypassCache:true and are never stale regardless of this value.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // ConcurrentDictionary (not plain Dictionary): Blazor renders sibling
    // components' async lifecycle methods (e.g. several RequirePermission checks
    // on one page) without a barrier, and the DB fills below use
    // ConfigureAwait(false), so the cache writes run on thread-pool threads in
    // parallel. A plain Dictionary corrupts under concurrent TryInsert
    // ("non-concurrent collections must have exclusive access") on a cold
    // circuit. Each entry carries its fetch time so reads can honour the TTL.
    private readonly ConcurrentDictionary<CacheKey, CacheEntry<IReadOnlyList<RoleAssignment>>> _assignmentCache = new();
    // WP3-c — keyed by (UserId, SpaceId), NOT just UserId: admin-ness is now
    // scope-dependent (a Space-pinned AdministerSystem is god mode only inside
    // that Space), so a per-user bool would serve the first Space's answer to
    // every other Space for the TTL — a cross-Space grant/deny leak.
    private readonly ConcurrentDictionary<CacheKey, CacheEntry<bool>> _systemAdminCache = new();

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        Permission permission,
        PermissionScope scope = default,
        bool bypassCache = false,
        bool strictScope = false,
        CancellationToken ct = default)
    {
        // A Space-restricted API key (M13.C.4) is caged to its bound Space —
        // checked BEFORE the sysadmin short-circuit so an admin-owned
        // restricted key stays caged. System-wide checks (null SpaceId in the
        // scope) are denied too: a restricted key must never exercise
        // instance-wide permissions.
        if (IsOutsideApiKeyRestriction(user, scope))
        {
            return false;
        }

        // AdministerSystem is god mode — short-circuits every check WITHIN the
        // assignment's own reach (WP3-c): a system-scope (Space-less) assignment
        // short-circuits everything, a Space-pinned one only checks against its
        // own Space. Granted by being on a team whose role assignments include
        // that permission.
        if (await UserIsSystemAdminAsync(user, scope.SpaceId, bypassCache, ct).ConfigureAwait(false))
        {
            return true;
        }

        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return false;
        }

        var assignments = await GetCachedAssignmentsAsync(userId.Value, scope.SpaceId, bypassCache, ct)
            .ConfigureAwait(false);

        // Short-circuit: stop at the first matching assignment that grants
        // the requested permission. Avoids walking the full set when one
        // hit is enough.
        foreach (var a in assignments)
        {
            if (a.Role.GrantedPermissions.Contains(permission)
                && RoleAssignmentScopeMatcher.Matches(a, scope, strictScope))
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

        // Space-restricted API key outside its Space → no permissions at all
        // (mirrors HasPermissionAsync's cage).
        if (IsOutsideApiKeyRestriction(user, scope))
        {
            return new HashSet<Permission>();
        }

        // System admin: union of every defined permission. Cheap because
        // Permission is a bounded enum. WP3-c — scoped to the requested Space,
        // so a Space-pinned AdministerSystem yields the full set only there.
        if (await UserIsSystemAdminAsync(user, scope.SpaceId, bypassCache: false, ct).ConfigureAwait(false))
        {
            return new HashSet<Permission>(Enum.GetValues<Permission>());
        }

        var assignments = await GetCachedAssignmentsAsync(userId.Value, scope.SpaceId, bypassCache: false, ct)
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

    public async Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // AdministerSystem reaches every Active Space — but only from a
        // system-scope (Space-less) assignment, so spaceId: null here (WP3-c).
        // A Space-PINNED AdministerSystem does not take this branch; its Space
        // is reached through the ordinary membership sweep below, because the
        // pinned assignment itself has a non-null SpaceId.
        if (await UserIsSystemAdminAsync(user, spaceId: null, bypassCache: false, ct).ConfigureAwait(false))
        {
            var allActive = await db.Spaces
                .Where(s => s.Status == SpaceStatus.Active)
                .Select(s => s.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return ApplyApiKeyRestriction(user, allActive.ToHashSet());
        }

        var teamIds = await GetUserTeamIdsAsync(db, userId.Value, ct).ConfigureAwait(false);
        if (teamIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // Distinct Space-scoped assignments reachable via real membership. A null
        // SpaceId pins no Space (system-wide grant) so it's excluded here.
        var assignedSpaceIds = await db.RoleAssignments
            .IgnoreQueryFilters() // RoleAssignment isn't ISpaceScoped; be explicit
            .Where(a => teamIds.Contains(a.TeamId) && a.SpaceId != null)
            .Select(a => a.SpaceId!.Value)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (assignedSpaceIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // Keep only Active Spaces (a member of a Suspended/Archived Space can't
        // act in it).
        var activeAccessible = await db.Spaces
            .Where(s => s.Status == SpaceStatus.Active && assignedSpaceIds.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ApplyApiKeyRestriction(user, activeAccessible.ToHashSet());
    }

    /// <summary>
    /// The Space a request is caged to, IFF it authenticated via a
    /// Space-restricted API key. Read ONLY from the identity whose
    /// AuthenticationType is the ApiKey scheme — never a bare claim lookup.
    /// <para>
    /// ASP.NET Core's <c>IPolicyEvaluator</c> authenticates every scheme named
    /// on a policy and MERGES each success into <c>HttpContext.User</c>
    /// (<c>SecurityHelper.MergeUserPrincipal</c>). Our default/fallback/perm:
    /// policies all name both the cookie and ApiKey schemes, so a cookie
    /// session that also carries a stray restricted <c>X-Api-Key</c> would end
    /// up with the <c>kraken:apikey_space</c> claim merged onto the
    /// cookie-primary principal — a plain <c>FindFirst</c> would then silently
    /// cage a legitimate browser session. Matching by AuthenticationType keeps
    /// the cage scoped to genuine API-key requests.
    /// </para>
    /// </summary>
    private static Guid? GetApiKeyRestriction(ClaimsPrincipal user)
    {
        // The restriction must come from an identity that authenticated via the
        // ApiKey scheme — never a bare claim lookup.
        var apiKeyIdentity = user.Identities.FirstOrDefault(
            i => string.Equals(i.AuthenticationType, KrakenAuthSchemes.ApiKey, StringComparison.Ordinal));
        if (apiKeyIdentity is null)
        {
            return null;
        }

        // If the request ALSO authenticated interactively (cookie / OIDC), that
        // browser session is authoritative and a stray restricted X-Api-Key
        // header must NOT silently cage it. IPolicyEvaluator merges every
        // succeeding scheme's identity into the principal, so honour the cage
        // only for a PURE API-key request (no other authenticated identity).
        var hasInteractiveIdentity = user.Identities.Any(i =>
            i.IsAuthenticated
            && !string.Equals(i.AuthenticationType, KrakenAuthSchemes.ApiKey, StringComparison.Ordinal));
        if (hasInteractiveIdentity)
        {
            return null;
        }

        var raw = apiKeyIdentity.FindFirst(KrakenClaimTypes.ApiKeySpace)?.Value;
        return Guid.TryParse(raw, out var space) ? space : null;
    }

    /// <summary>
    /// True when the request authenticated with a Space-restricted API key and
    /// the requested scope falls outside that Space (including system-wide
    /// scopes). See <see cref="GetApiKeyRestriction"/>.
    /// </summary>
    private static bool IsOutsideApiKeyRestriction(ClaimsPrincipal user, PermissionScope scope)
    {
        var restrictedSpace = GetApiKeyRestriction(user);
        return restrictedSpace is not null && scope.SpaceId != restrictedSpace;
    }

    /// <summary>Caps an accessible-Space set to a restricted key's one Space.</summary>
    private static HashSet<Guid> ApplyApiKeyRestriction(ClaimsPrincipal user, HashSet<Guid> spaces)
    {
        if (GetApiKeyRestriction(user) is { } restrictedSpace)
        {
            spaces.IntersectWith([restrictedSpace]);
        }

        return spaces;
    }

    // ── Cached assignment fetch ───────────────────────────────────────────────

    private async Task<IReadOnlyList<RoleAssignment>> GetCachedAssignmentsAsync(
        Guid userId, Guid? spaceId, bool bypassCache, CancellationToken ct)
    {
        var key = new CacheKey(userId, spaceId);

        // Fast path — a fresh (within-TTL) cache entry, no DB needed.
        if (!bypassCache
            && _assignmentCache.TryGetValue(key, out var cached)
            && IsFresh(cached.CachedAtUtc))
        {
            return cached.Value;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var teamIds = await GetUserTeamIdsAsync(db, userId, ct).ConfigureAwait(false);

        IReadOnlyList<RoleAssignment> assignments;
        if (teamIds.Count == 0)
        {
            assignments = [];
        }
        else
        {
            // Pull every role assignment for those teams matching the requested
            // Space (or system-wide assignments which apply to all Spaces).
            // We deliberately keep the raw assignments — scope-dimension
            // filtering is per-call, not per-fetch, so the same cache entry
            // serves many different scoped queries during one render.
            assignments = await db.RoleAssignments
                .IgnoreQueryFilters() // RoleAssignment isn't ISpaceScoped, but be explicit
                .Include(a => a.Role)
                // MUST eager-load scopes: the matcher reads assignment.Scopes,
                // and an unloaded collection reads as "no scopes = whole Space"
                // — a fail-open over-grant.
                .Include(a => a.Scopes)
                .Where(a => teamIds.Contains(a.TeamId))
                .Where(a => a.SpaceId == null || a.SpaceId == spaceId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        _assignmentCache[key] = new CacheEntry<IReadOnlyList<RoleAssignment>>(
            assignments, timeProvider.GetUtcNow());
        return assignments;
    }

    // ── Team membership resolution ────────────────────────────────────────────

    /// <summary>
    /// Public entry point over the same resolver RBAC uses (see
    /// <see cref="IPermissionEvaluator.GetUserTeamIdsAsync"/>). Deliberately NOT cached:
    /// its only caller is WP3's manual-intervention response path, which is an
    /// execution-time authorization decision — the same reason the UI action guard
    /// passes <c>bypassCache: true</c>. Removing a user from a responsible team must take
    /// effect immediately, not within <see cref="CacheTtl"/>.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> GetUserTeamIdsAsync(
        ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await GetUserTeamIdsAsync(db, userId.Value, ct).ConfigureAwait(false);
    }

    private static async Task<HashSet<Guid>> GetUserTeamIdsAsync(
        KrakenDbContext db, Guid userId, CancellationToken ct)
    {
        // a. Explicit team_members rows.
        var explicitTeams = await db.TeamMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.TeamId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var teamIds = new HashSet<Guid>(explicitTeams);

        // b. External-group matches via TeamExternalGroup.
        //    Group memberships are persisted on ApplicationUser.ExternalGroups
        //    at OIDC sign-in time so they survive Identity security-stamp
        //    refreshes.  We query the DB here rather than relying on cookie
        //    claims so the mapping stays current even if a team's external-
        //    group list changes between sign-ins.
        var appUser = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LastOidcProviderId, u.ExternalGroups })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(appUser?.ExternalGroups))
        {
            var groups = appUser.ExternalGroups.Split('|',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var externalTeams = await db.TeamExternalGroups
                .AsNoTracking()
                .Where(eg => groups.Contains(eg.GroupClaim)
                          && (eg.IdentityProviderId == null
                              || eg.IdentityProviderId == appUser.LastOidcProviderId))
                .Select(eg => eg.TeamId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var t in externalTeams)
            {
                teamIds.Add(t);
            }
        }

        // c. "Everyone" teams. The SYSTEM-level Everyone team (SpaceId == null) is
        //    a virtual team every authenticated user belongs to. A per-Space
        //    Everyone team must NOT be virtual-for-all: doing so hands every user
        //    that team's Space-scoped ProjectViewer grant on EVERY Space — total
        //    cross-tenant read. Include a per-Space Everyone team ONLY for Spaces
        //    the user is a real member of (i.e. belongs to a concrete team scoped
        //    to that Space via a/b above). System admins are unaffected:
        //    HasPermission/GetPermissions short-circuit before assignment lookup.
        var memberSpaceIds = await db.Teams
            .Where(t => teamIds.Contains(t.Id) && t.SpaceId != null)
            .Select(t => t.SpaceId!.Value)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var everyoneTeams = await db.Teams
            .Where(t => t.IsEveryoneTeam
                     && (t.SpaceId == null || memberSpaceIds.Contains(t.SpaceId!.Value)))
            .Select(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var t in everyoneTeams)
        {
            teamIds.Add(t);
        }

        return teamIds;
    }

    // ── System admin shortcut ─────────────────────────────────────────────────

    /// <summary>
    /// WP3-c — true when the user holds <see cref="Permission.AdministerSystem"/>
    /// with reach over <paramref name="spaceId"/>. The assignment's own
    /// <see cref="RoleAssignment.SpaceId"/> is honoured: a system-scope
    /// (Space-less) assignment is god mode everywhere, a Space-pinned one only
    /// inside its Space. <paramref name="spaceId"/> = null means a SYSTEM-wide
    /// question (Hangfire dashboard, maintenance bypass, "reach every Space"),
    /// which only a system-scope assignment may answer — previously a grant
    /// pinned to ONE Space short-circuited every check globally.
    /// </summary>
    private async Task<bool> UserIsSystemAdminAsync(
        ClaimsPrincipal user, Guid? spaceId, bool bypassCache, CancellationToken ct)
    {
        var userId = TryGetUserId(user);
        if (userId is null)
        {
            return false;
        }

        // Fast path — a fresh (within-TTL) cache entry, no DB needed. Keyed per
        // (user, Space): the answer differs across Spaces now.
        var key = new CacheKey(userId.Value, spaceId);
        if (!bypassCache
            && _systemAdminCache.TryGetValue(key, out var cached)
            && IsFresh(cached.CachedAtUtc))
        {
            return cached.Value;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var teamIds = await GetUserTeamIdsAsync(db, userId.Value, ct).ConfigureAwait(false);

        bool isAdmin;
        if (teamIds.Count == 0)
        {
            isAdmin = false;
        }
        else
        {
            // GrantedPermissions is a jsonb column — EF Core cannot translate
            // Enumerable.Contains inside a server-side predicate. Pull only the
            // permissions lists into memory and evaluate in C#.
            // IgnoreQueryFilters is query-compilation-WIDE (EF Core 10, see
            // execution-engine.md §6), so the SpaceId reach predicate below is
            // deliberately explicit — same shape as GetCachedAssignmentsAsync.
            var permissionLists = await db.RoleAssignments
                .IgnoreQueryFilters()
                .Where(a => teamIds.Contains(a.TeamId))
                .Where(a => a.SpaceId == null || a.SpaceId == spaceId)
                .Select(a => a.Role.GrantedPermissions)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            isAdmin = permissionLists.Any(p => p.Contains(Permission.AdministerSystem));
        }

        _systemAdminCache[key] = new CacheEntry<bool>(isAdmin, timeProvider.GetUtcNow());
        return isAdmin;
    }

    private bool IsFresh(DateTimeOffset cachedAtUtc) =>
        timeProvider.GetUtcNow() - cachedAtUtc < CacheTtl;

    /// <summary>WP3-b — the shared extraction (Core <c>ClaimsPrincipalExtensions</c>).
    /// <c>UserIdClaim</c> IS <c>ClaimTypes.NameIdentifier</c>, so this reads the same
    /// claim; keeping a private copy is how five of them drifted apart.</summary>
    private static Guid? TryGetUserId(ClaimsPrincipal user)
        => user.ResolveUserId();

    private readonly record struct CacheEntry<T>(T Value, DateTimeOffset CachedAtUtc);

    private readonly record struct CacheKey(Guid UserId, Guid? SpaceId);
}

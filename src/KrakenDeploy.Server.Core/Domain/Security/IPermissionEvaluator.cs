using System.Security.Claims;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Evaluates whether a <see cref="ClaimsPrincipal"/> has a given
/// <see cref="Permission"/> within an optional <see cref="PermissionScope"/>.
/// <para>
/// Backed by the M10 RBAC model: a user has a permission iff some
/// <see cref="RoleAssignment"/> grants it through a <see cref="Role"/>
/// attached to a <see cref="Team"/> the user is a member of (explicitly via
/// <c>TeamMember</c>, dynamically via <c>TeamExternalGroup</c>, or
/// implicitly via the "Everyone" team), <em>and</em> the assignment's scope
/// dimensions either match the requested scope or are empty (= "all").
/// </para>
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>
    /// True when <paramref name="user"/> has <paramref name="permission"/>
    /// within <paramref name="scope"/>. Always true for users with the
    /// <see cref="Permission.AdministerSystem"/> permission anywhere.
    /// <para>
    /// Set <paramref name="bypassCache"/> for an authoritative, never-stale
    /// read — used by execution-time action guards so a stale UI cache cannot
    /// authorize a privileged operation. UI rendering checks leave it
    /// <c>false</c> to keep the per-render cache (bounded by the evaluator's TTL).
    /// </para>
    /// </summary>
    Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        Permission permission,
        PermissionScope scope = default,
        bool bypassCache = false,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the union of all permissions <paramref name="user"/> has within
    /// <paramref name="scope"/>. Useful for UI rendering decisions like
    /// "show the Edit button only if the user can actually edit". Cached
    /// per-request so repeated calls are cheap.
    /// </summary>
    Task<IReadOnlySet<Permission>> GetPermissionsAsync(
        ClaimsPrincipal user,
        PermissionScope scope = default,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the set of Space ids <paramref name="user"/> may access. The
    /// authoritative source for the hard tenant boundary: every <em>Active</em>
    /// Space when the user holds <see cref="Permission.AdministerSystem"/>;
    /// otherwise the Active Spaces they reach through a <em>real</em> team
    /// membership (explicit <c>TeamMember</c>, external-group match, or a
    /// per-Space "Everyone" team they belong to). System-wide role assignments
    /// (<c>RoleAssignment.SpaceId == null</c>) pin no Space and are excluded —
    /// they are only meaningful for the AdministerSystem short-circuit above.
    /// <para>
    /// Used to gate the Space switcher, the <c>/api/spaces</c> listing, and the
    /// active-space cookie/circuit resolution. Returns an empty set for an
    /// anonymous or unknown principal.
    /// </para>
    /// </summary>
    Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
        ClaimsPrincipal user,
        CancellationToken ct = default);
}

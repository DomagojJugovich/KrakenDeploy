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
    /// within <paramref name="scope"/>. Always true when the user holds
    /// <see cref="Permission.AdministerSystem"/> with reach over the scope's
    /// Space (WP3-c): a system-scope assignment (<c>RoleAssignment.SpaceId ==
    /// null</c>) is god mode everywhere, a Space-pinned one only inside its
    /// own Space.
    /// <para>
    /// Set <paramref name="bypassCache"/> for an authoritative, never-stale
    /// read — used by execution-time action guards so a stale UI cache cannot
    /// authorize a privileged operation. UI rendering checks leave it
    /// <c>false</c> to keep the per-render cache (bounded by the evaluator's TTL).
    /// </para>
    /// <para>
    /// Set <paramref name="strictScope"/> (T1-8) for WRITE/execute checks: a
    /// dimension the grant RESTRICTS but the caller left <c>null</c> fails
    /// closed instead of optimistically passing, so the caller must supply the
    /// concrete Project/Environment/Tenant. Leave it <c>false</c> for broad
    /// read/UI checks (the "could I act somewhere?" semantics).
    /// </para>
    /// </summary>
    Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        Permission permission,
        PermissionScope scope = default,
        bool bypassCache = false,
        bool strictScope = false,
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
    /// Space when the user holds <see cref="Permission.AdministerSystem"/>
    /// through a system-scope (Space-less) assignment; otherwise the Active
    /// Spaces they reach through a <em>real</em> team membership (explicit
    /// <c>TeamMember</c>, external-group match, or a per-Space "Everyone" team
    /// they belong to). A Space-PINNED AdministerSystem assignment reaches its
    /// own Space through that membership sweep like any other Space-scoped
    /// grant (WP3-c). System-wide role assignments (<c>RoleAssignment.SpaceId
    /// == null</c>) pin no Space and are excluded from the sweep — they are
    /// only meaningful for the AdministerSystem short-circuit above.
    /// <para>
    /// Used to gate the Space switcher, the <c>/api/spaces</c> listing, and the
    /// active-space cookie/circuit resolution. Returns an empty set for an
    /// anonymous or unknown principal.
    /// </para>
    /// </summary>
    Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
        ClaimsPrincipal user,
        CancellationToken ct = default);

    /// <summary>
    /// The set of <see cref="Team"/> ids <paramref name="user"/> belongs to, merging all
    /// three membership sources exactly as permission evaluation does: explicit
    /// <c>TeamMember</c> rows, <c>TeamExternalGroup</c> matches against the user's
    /// persisted IdP groups, and the applicable "Everyone" teams (the system-level one
    /// always; a per-Space one only for Spaces the user really belongs to).
    /// <para>
    /// Exposed for authorization that turns on team membership DIRECTLY rather than on a
    /// permission — currently WP3's manual-intervention gate, whose responsible-team
    /// list is per-step data chosen by the process author and so cannot be modelled as a
    /// <see cref="Permission"/>. It lives on this interface, rather than being
    /// reimplemented by the caller, because the three sources have non-obvious rules
    /// (external groups come from the DB, not from cookie claims, so a team's group list
    /// can change between sign-ins; a per-Space Everyone team must NOT be virtual for
    /// everybody) and a second copy would drift from the RBAC one.
    /// </para>
    /// <para>Returns an empty set for an anonymous or unknown principal.</para>
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUserTeamIdsAsync(
        ClaimsPrincipal user,
        CancellationToken ct = default);
}

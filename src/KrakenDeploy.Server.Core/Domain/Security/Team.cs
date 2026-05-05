using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// A named group of users (and/or external IdP groups) that role assignments
/// target. Maps 1:1 to the Octopus Deploy "Team" concept.
/// <para>
/// A Team can be:
/// <list type="bullet">
///   <item><b>System-level</b> (<see cref="SpaceId"/> = <c>null</c>): visible
///         everywhere. Used for "Kraken Administrators" and the system-wide
///         "Everyone" team.</item>
///   <item><b>Space-scoped</b> (<see cref="SpaceId"/> = a Space): only visible
///         and assignable within that one Space. Used for "Space Managers",
///         "Project Deployers", etc.</item>
/// </list>
/// </para>
/// <para>
/// Members come from two sources merged at evaluation time:
/// <list type="number">
///   <item>Explicit users in <see cref="Members"/>.</item>
///   <item>External directory groups in <see cref="ExternalGroups"/> — at sign-in
///         time, the user's group claims (from OIDC / LDAP) are matched against
///         these to dynamically add them to the team.</item>
/// </list>
/// </para>
/// </summary>
public class Team : AuditableEntity
{
    /// <summary>
    /// FK to the owning <see cref="Spaces.Space"/>; <c>null</c> for
    /// system-level teams. Intentionally <em>not</em> using
    /// <see cref="ISpaceScoped"/> because nullable scoping doesn't fit the
    /// global-query-filter pattern (system teams must remain visible from
    /// every Space).
    /// </summary>
    public Guid? SpaceId { get; set; }

    /// <summary>Display name, unique within the Space (or system-wide for system teams).</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// True for teams seeded by <c>BuiltInTeamSeeder</c>. Cannot be deleted;
    /// memberships and role assignments are re-applied on startup.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// True for the implicit "Everyone" team — every authenticated user is a
    /// virtual member without an explicit <see cref="TeamMember"/> row.
    /// </summary>
    public bool IsEveryoneTeam { get; set; }

    public ICollection<TeamMember> Members { get; set; } = [];
    public ICollection<TeamExternalGroup> ExternalGroups { get; set; } = [];
    public ICollection<RoleAssignment> RoleAssignments { get; set; } = [];
}

/// <summary>Many-to-many join row: explicit user membership in a team.</summary>
public class TeamMember
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary>UTC timestamp when the user was added to the team.</summary>
    public DateTimeOffset AddedUtc { get; set; }
}

/// <summary>
/// External directory group (AD security group, Azure AD / Entra group, Okta
/// group, LDAP group) that grants implicit membership in a <see cref="Team"/>.
/// At sign-in the OIDC / LDAP token's group claim values are matched against
/// <see cref="GroupClaim"/>; matches add the user to the team for that session.
/// </summary>
public class TeamExternalGroup : Entity
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>
    /// FK to the IdP that issued this group — <c>null</c> means "any provider"
    /// (rare; usually you scope to one IdP so different IdPs don't collide on
    /// a generic name like "developers").
    /// </summary>
    public Guid? IdentityProviderId { get; set; }
    public IdentityProvider? IdentityProvider { get; set; }

    /// <summary>
    /// The exact value of the group claim as the IdP issues it
    /// (e.g. <c>"S-1-5-21-...-512"</c>, <c>"developers@example.com"</c>,
    /// <c>"DOMAIN\\Developers"</c>, or a UUID for Entra/Okta).
    /// </summary>
    public required string GroupClaim { get; set; }

    /// <summary>Cached friendly name for UI display (e.g. "Developers").</summary>
    public string? DisplayName { get; set; }
}

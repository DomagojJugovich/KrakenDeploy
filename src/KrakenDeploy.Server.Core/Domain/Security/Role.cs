using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// A named bundle of <see cref="Permission"/> atoms. Roles are
/// <em>system-level</em> (not scoped to a Space) — the same Role can be
/// assigned to different Teams in different Spaces with different scopes
/// via <see cref="RoleAssignment"/>.
/// <para>
/// Built-in roles (<see cref="IsBuiltIn"/> = <c>true</c>) are seeded on
/// startup and cannot be deleted. Customers can author additional custom
/// Roles with their own permission combinations.
/// </para>
/// <para>
/// Maps 1:1 to the Octopus Deploy "User Role" concept.
/// </para>
/// </summary>
public class Role : AuditableEntity
{
    /// <summary>Display name, unique system-wide.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Permissions this role grants. Stored as a <c>jsonb</c> array of integer
    /// values (the <see cref="Permission"/> enum's underlying ints) so adding
    /// a new permission to a built-in role doesn't require a schema change.
    /// </summary>
    public List<Permission> GrantedPermissions { get; set; } = [];

    /// <summary>
    /// True for roles seeded by <c>BuiltInRoleSeeder</c> on startup. These
    /// cannot be deleted; their permission set is re-applied on every server
    /// startup so changes from the seeder ship with the upgrade.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// True when this role's permissions can ONLY be granted at the system
    /// level (i.e. without a Space scope) — e.g. <see cref="Permission.AdministerSystem"/>,
    /// <see cref="Permission.SpaceCreate"/>. The UI hides the scope selector
    /// when assigning a system-only role.
    /// </summary>
    public bool IsSystemOnly { get; set; }
}

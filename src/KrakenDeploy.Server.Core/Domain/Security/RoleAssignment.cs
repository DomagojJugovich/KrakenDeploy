using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Grants a <see cref="Role"/> to a <see cref="Team"/> within an optional
/// scope. The scope is a composite of independent dimensions (Project Groups,
/// Projects, Environments, Tenants, Tags) — within a dimension the
/// list values are OR'd; between dimensions they're AND'd.
/// <para>
/// An empty list for a dimension means "all in this Space" for that dimension.
/// Example interpretation:
/// <code>
/// Team:        Web Deployers
/// Role:        Project Deployer
/// Space:       Production
/// Projects:    [WebApp, ApiGateway]
/// Environments:[Prod, Staging]
/// Tenants:     []        // = all tenants
/// </code>
/// → "Web Deployers may exercise Project Deployer permissions on WebApp or
/// ApiGateway, in Prod or Staging, for any tenant in the Production Space."
/// </para>
/// <para>
/// Maps 1:1 to the Octopus Deploy "scoped role assignment" concept.
/// </para>
/// </summary>
public class RoleAssignment : AuditableEntity
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>
    /// Space the assignment applies to. <c>null</c> means system-wide
    /// (only valid when the granted role is <see cref="Role.IsSystemOnly"/>
    /// or for cross-Space admin teams).
    /// </summary>
    public Guid? SpaceId { get; set; }

    // ── Scope dimensions (jsonb arrays of Guid) ──────────────────────────────
    // Empty array = "all in this Space" for that dimension.

    public List<Guid> ProjectGroupIds { get; set; } = [];
    public List<Guid> ProjectIds { get; set; } = [];
    public List<Guid> EnvironmentIds { get; set; } = [];
    public List<Guid> TenantIds { get; set; } = [];

    /// <summary>Extended-tag-set tag ids (renamed from <c>TenantTagIds</c> —
    /// tags are polymorphic now, no longer tenant-owned). Dormant: no UI/API
    /// writes this dimension yet; the matcher honours it when populated.</summary>
    public List<Guid> TagIds { get; set; } = [];

    /// <summary>
    /// True when every scope dimension is empty — the assignment grants the
    /// role across the entire Space (or system-wide if <see cref="SpaceId"/>
    /// is null).
    /// </summary>
    public bool IsUnscoped =>
        ProjectGroupIds.Count == 0 &&
        ProjectIds.Count == 0 &&
        EnvironmentIds.Count == 0 &&
        TenantIds.Count == 0 &&
        TagIds.Count == 0;
}

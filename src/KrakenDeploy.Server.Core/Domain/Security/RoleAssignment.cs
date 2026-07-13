using System.ComponentModel.DataAnnotations.Schema;
using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Grants a <see cref="Role"/> to a <see cref="Team"/> within an optional
/// scope. The scope is a composite of independent dimensions (Project Groups,
/// Projects, Environments, Tenants) — within a dimension the values are OR'd;
/// between dimensions they're AND'd.
/// <para>
/// Scope values live in the <c>role_assignment_scopes</c> child table (one row
/// per (dimension, id)) with real per-dimension FKs, so a deleted project /
/// environment / tenant / group cascades out of every grant. NO rows for a
/// dimension means "all in this Space" for that dimension. Example:
/// <code>
/// Team:        Web Deployers
/// Role:        Project Deployer
/// Space:       Production
/// Projects:    [WebApp, ApiGateway]
/// Environments:[Prod, Staging]
/// Tenants:     (no rows)   // = all tenants
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

    /// <summary>
    /// Scope restriction rows (one per (dimension, id)). Empty collection =
    /// unscoped (whole Space). Must be eager-loaded wherever the matcher runs —
    /// a detached (unloaded) collection reads as "no scopes = all", a fail-open
    /// over-grant.
    /// </summary>
    public ICollection<RoleAssignmentScope> Scopes { get; set; } = [];

    // ── Per-dimension views over Scopes (no rows = "all") ────────────────────
    // Read-only projections so existing callers (matcher, team-detail UI) keep
    // reading ProjectIds/EnvironmentIds/etc. unchanged after the jsonb→child
    // move. NotMapped: the data lives in role_assignment_scopes.

    [NotMapped]
    public IReadOnlyList<Guid> ProjectGroupIds =>
        Scopes.Where(s => s.ProjectGroupId.HasValue).Select(s => s.ProjectGroupId!.Value).ToList();

    [NotMapped]
    public IReadOnlyList<Guid> ProjectIds =>
        Scopes.Where(s => s.ProjectId.HasValue).Select(s => s.ProjectId!.Value).ToList();

    [NotMapped]
    public IReadOnlyList<Guid> EnvironmentIds =>
        Scopes.Where(s => s.EnvironmentId.HasValue).Select(s => s.EnvironmentId!.Value).ToList();

    [NotMapped]
    public IReadOnlyList<Guid> TenantIds =>
        Scopes.Where(s => s.TenantId.HasValue).Select(s => s.TenantId!.Value).ToList();

    /// <summary>
    /// True when there are no scope rows — the assignment grants the role
    /// across the entire Space (or system-wide if <see cref="SpaceId"/> is null).
    /// </summary>
    [NotMapped]
    public bool IsUnscoped => Scopes.Count == 0;
}

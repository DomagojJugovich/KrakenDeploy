using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Spaces;

/// <summary>
/// A Space is a top-level partition of the system. Every space-scoped entity
/// (Project, Environment, DeploymentTarget, Tenant, Variable, etc.) belongs to
/// exactly one Space; users can belong to multiple Spaces with per-Space roles.
/// <para>
/// On-prem installs typically have only the <see cref="WellKnown.DefaultSpaceId"/>
/// Space and the UI hides the Space switcher entirely. Cloud SaaS uses one Space
/// per customer workspace.
/// </para>
/// <para>
/// Maps 1:1 to the Octopus Deploy "Space" concept (<em>not</em> to Octopus
/// "Tenant" — the existing <c>Tenant</c> entity already covers that, representing
/// a deployment-target customer <em>within</em> a Space).
/// </para>
/// </summary>
public class Space : AuditableEntity
{
    /// <summary>URL-friendly identifier, lower-case ASCII + hyphens, unique.</summary>
    public required string Slug { get; set; }

    /// <summary>Display name shown in the Space switcher.</summary>
    public required string Name { get; set; }

    /// <summary>Optional admin-friendly description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True for the bootstrap Space (<see cref="WellKnown.DefaultSpaceId"/>).
    /// At most one row has this set; the Default Space cannot be deleted.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>Lifecycle state of the Space — drives access and billing logic.</summary>
    public SpaceStatus Status { get; set; } = SpaceStatus.Active;
}

/// <summary>Lifecycle state for a <see cref="Space"/>.</summary>
public enum SpaceStatus
{
    /// <summary>Normal operation.</summary>
    Active = 0,

    /// <summary>Read-only — users can sign in and view but not modify.</summary>
    Suspended = 1,

    /// <summary>Marked for deletion; data purge runs asynchronously.</summary>
    Archived = 2,
}

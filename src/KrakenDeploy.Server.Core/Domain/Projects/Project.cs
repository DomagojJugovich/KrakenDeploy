using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Core.Domain.Projects;

public class Project : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>
    /// FK to the owning <see cref="ProjectGroup"/> (the Project's folder).
    /// Nullable during the transitional period after the M10 migration adds
    /// the column but before the Default Project Group seeder runs; in
    /// steady state every Project belongs to exactly one Group.
    /// </summary>
    public Guid? ProjectGroupId { get; set; }
    public ProjectGroup? ProjectGroup { get; set; }

    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    // The deployment process is a polymorphic Process row (owner_kind=Project,
    // owner_id=this.Id) with no owner FK — resolve it via ProcessService, not a
    // navigation property.

    /// <summary>Variable set for this project (one-to-one, created lazily).</summary>
    public VariableSet? VariableSet { get; set; }

    /// <summary>Tenants connected to this project.</summary>
    public ICollection<Tenant> Tenants { get; set; } = [];

    /// <summary>
    /// Default lifecycle applied to all channels that don't specify one.
    /// <c>null</c> = no lifecycle gates enforced by default.
    /// </summary>
    public Guid? LifecycleId { get; set; }
    public Lifecycle? Lifecycle { get; set; }

    /// <summary>Channels defined for this project (at least one default always exists).</summary>
    public ICollection<Channel> Channels { get; set; } = [];
}

using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Tenants;

/// <summary>
/// A tenant represents a customer or business unit that is deployed to independently.
/// Tenants can be connected to projects and tagged onto deployment targets.
/// Each tenant optionally owns a <see cref="Variables.VariableSet"/> for common variables
/// that supplement project-level scoping.
/// </summary>
public class Tenant : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>URL-safe identifier (e.g. "acme-corp").</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Optional variable set holding tenant-common variables.
    /// When set during resolution, these variables are merged with the project
    /// variable set, with tenant-scoped project variables taking precedence.
    /// </summary>
    public Guid? VariableSetId { get; set; }

    /// <summary>Tag sets owned by this tenant.</summary>
    public ICollection<TagSet> TagSets { get; set; } = [];

    /// <summary>Projects this tenant is connected to.</summary>
    public ICollection<Project> Projects { get; set; } = [];
}

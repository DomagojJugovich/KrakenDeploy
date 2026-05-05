using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Projects;

/// <summary>
/// A folder grouping projects within a Space, and a dimension on which Role
/// Assignments can be scoped. Maps 1:1 to the Octopus Deploy "Project Group"
/// concept.
/// <para>
/// Every Space gets a "Default Project Group" auto-created on first run; new
/// projects land there unless explicitly moved.
/// </para>
/// </summary>
public class ProjectGroup : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>URL-friendly identifier, unique within the Space.</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Display ordering on the Projects page (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// True for the bootstrap Project Group that's auto-created with a Space.
    /// Cannot be deleted; can be renamed.
    /// </summary>
    public bool IsDefault { get; set; }
}

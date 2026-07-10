using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Tags;

/// <summary>
/// A single predefined tag within a <see cref="TagSet"/> (e.g. "Production"
/// in set "Tier"). Applied to entities via <see cref="TagApplication"/>.
/// Consumers reference tags by <see cref="Entity.Id"/>; the canonical
/// "TagSetName/TagName" string (<see cref="TagCanonical"/>) is a display /
/// Octopus-parity format only.
/// </summary>
public class Tag : AuditableEntity, ISpaceScoped
{
    /// <summary>Inherited from the owning TagSet; stamped on insert so by-id
    /// reads/mutations are Space-safe.</summary>
    public Guid SpaceId { get; set; }

    public Guid TagSetId { get; set; }
    public TagSet TagSet { get; set; } = null!;

    /// <summary>Unique within the set.</summary>
    public required string Name { get; set; }

    /// <summary>Optional CSS/hex colour for UI display (e.g. "#e63946").</summary>
    public string? Color { get; set; }

    /// <summary>Optional per-tag description shown when applying tags.</summary>
    public string? Description { get; set; }

    /// <summary>Manual display ordering within the set (ascending).</summary>
    public int SortOrder { get; set; }
}

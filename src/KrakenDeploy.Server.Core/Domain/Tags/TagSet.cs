using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Tags;

/// <summary>
/// A Space-level named group of tags (Octopus extended tag sets — e.g.
/// "Hosting", "Tier", "Region"). A set declares which entity kinds it applies
/// to (<see cref="Scopes"/>) and its selection cardinality (<see cref="Type"/>).
/// Tags are applied to entities via <see cref="TagApplication"/> rows.
/// <para>
/// This replaces the earlier tenant-owned model (a set no longer belongs to a
/// tenant; tenants are just one taggable kind among five).
/// </para>
/// </summary>
public class TagSet : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>Unique per Space.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Display ordering across the Space's sets (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>Selection cardinality — see <see cref="TagSetType"/>.
    /// Changing it on a populated set is validated by the service (blocked
    /// while existing applications violate the new cardinality).</summary>
    public TagSetType Type { get; set; } = TagSetType.MultiSelect;

    /// <summary>Entity kinds this set applies to (multi-scope allowed). The
    /// service refuses applications to kinds outside this list and cascades
    /// removals only behind an explicit force flag.</summary>
    public List<TaggableEntityKind> Scopes { get; set; } = [];

    /// <summary>Predefined tags (empty for <see cref="TagSetType.FreeText"/> sets).</summary>
    public ICollection<Tag> Tags { get; set; } = [];
}

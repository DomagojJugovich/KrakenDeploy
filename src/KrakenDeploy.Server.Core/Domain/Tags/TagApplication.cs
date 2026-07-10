using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Tags;

/// <summary>
/// One tag applied to one entity — the unified polymorphic link between a
/// <see cref="TagSet"/> and any taggable entity kind.
/// <list type="bullet">
///   <item>Select-type sets: <see cref="TagId"/> is set, <see cref="FreeTextValue"/> is null.</item>
///   <item><see cref="TagSetType.FreeText"/> sets: <see cref="TagId"/> is null,
///         <see cref="FreeTextValue"/> carries the entity's value (one per set per entity).</item>
/// </list>
/// <para>
/// <see cref="EntityId"/> is deliberately FK-less (it points at five different
/// tables); referential cleanup is handled by
/// <c>TagApplicationCleanupInterceptor</c>, which removes an entity's
/// applications in the same save that deletes the entity.
/// </para>
/// <para>
/// <see cref="SetType"/> denormalizes the owning set's <see cref="TagSet.Type"/>
/// (stamped by the service on every write) so cardinality is enforceable by a
/// partial unique index on (TagSetId, EntityKind, EntityId) — a partial index
/// cannot consult the tag_sets table. A set's Type change rewrites these rows
/// (the service blocks the change while violations exist).
/// </para>
/// </summary>
public class TagApplication : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid TagSetId { get; set; }
    public TagSet TagSet { get; set; } = null!;

    /// <summary>The applied tag (null for FreeText sets).</summary>
    public Guid? TagId { get; set; }
    public Tag? Tag { get; set; }

    public TaggableEntityKind EntityKind { get; set; }

    /// <summary>Id of the tagged entity (tenant / project / environment /
    /// runbook / deployment target — per <see cref="EntityKind"/>).</summary>
    public Guid EntityId { get; set; }

    /// <summary>The arbitrary value for FreeText sets (null for select types).</summary>
    public string? FreeTextValue { get; set; }

    /// <summary>Denormalized <see cref="TagSet.Type"/> — see class remarks.</summary>
    public TagSetType SetType { get; set; }
}

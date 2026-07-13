using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Shared configuration helpers for entities that implement <see cref="ISpaceScoped"/>.
/// Apply <see cref="ConfigureSpaceScope{T}"/> from each entity's
/// <c>IEntityTypeConfiguration&lt;T&gt;.Configure</c> method to add the
/// <c>SpaceId</c> property metadata, an index on <c>SpaceId</c> for query-filter
/// performance, and the FK to <c>spaces.id</c> with <see cref="DeleteBehavior.Restrict"/>
/// (deleting a Space requires the caller to first move or delete its contents —
/// no implicit cascade across the entire entity graph).
/// </summary>
public static class SpaceScopedConfigurationExtensions
{
    /// <param name="addSpaceIdIndex">
    /// When <c>true</c> (default) a standalone index on <c>space_id</c> is created for
    /// the query filter. Pass <c>false</c> when the entity already has a composite index
    /// whose leading column is <c>space_id</c> (e.g. <c>(space_id, created_utc)</c>) — the
    /// composite already serves the filter, so a standalone index would be redundant.
    /// The FK to <c>spaces</c> is added regardless.
    /// </param>
    public static EntityTypeBuilder<T> ConfigureSpaceScope<T>(
        this EntityTypeBuilder<T> builder, bool addSpaceIdIndex = true)
        where T : class, ISpaceScoped
    {
        builder.Property(x => x.SpaceId).IsRequired();
        if (addSpaceIdIndex)
        {
            builder.HasIndex(x => x.SpaceId);
        }

        builder.HasOne<Space>()
            .WithMany()
            .HasForeignKey(x => x.SpaceId)
            .OnDelete(DeleteBehavior.Restrict);

        return builder;
    }

    /// <summary>
    /// Configures Space scoping for a <em>child</em> entity whose <c>space_id</c> is
    /// guaranteed transitively through a required composite parent FK of the form
    /// <c>(space_id, parent_id) → parent(space_id, id)</c>. Marks <c>space_id</c>
    /// required but adds <b>neither</b> the direct FK to <c>spaces</c> (the composite
    /// parent FK enforces <c>space_id</c> integrity transitively — the parent's own
    /// direct <c>spaces</c> FK closes the chain) <b>nor</b> a standalone <c>space_id</c>
    /// index (every composite parent FK's covering index already leads with
    /// <c>space_id</c>, so a standalone one is redundant).
    /// <para>
    /// The caller MUST configure the composite parent FK, e.g.
    /// <c>HasOne(x =&gt; x.Parent).WithMany(...).HasForeignKey(x =&gt; new { x.SpaceId, x.ParentId })
    /// .HasPrincipalKey(p =&gt; new { p.SpaceId, p.Id }).OnDelete(...)</c>. The
    /// <c>HasPrincipalKey</c> call auto-creates the <c>UNIQUE (space_id, id)</c>
    /// alternate key on the principal.
    /// </para>
    /// </summary>
    public static EntityTypeBuilder<T> ConfigureSpaceScopeAsChild<T>(
        this EntityTypeBuilder<T> builder)
        where T : class, ISpaceScoped
    {
        builder.Property(x => x.SpaceId).IsRequired();
        return builder;
    }
}

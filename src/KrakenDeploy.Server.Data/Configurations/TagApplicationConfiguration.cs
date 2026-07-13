using KrakenDeploy.Server.Core.Domain.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="TagApplication"/> — the unified polymorphic
/// tag↔entity link. <c>EntityId</c> has no FK (it points at five tables);
/// referential cleanup is <c>TagApplicationCleanupInterceptor</c>'s job.
/// Cardinality is DB-enforced via the partial unique index below — the last
/// line of defence against concurrent writers; the service validates first
/// for friendly errors.
/// </summary>
public class TagApplicationConfiguration : IEntityTypeConfiguration<TagApplication>
{
    public void Configure(EntityTypeBuilder<TagApplication> builder)
    {
        builder.ToTable("tag_applications");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.EntityKind).IsRequired();
        builder.Property(x => x.SetType).IsRequired();
        builder.Property(x => x.FreeTextValue).HasMaxLength(1000);

        // Composite Space FKs: the set and tag must live in the application's Space.
        builder.HasOne(x => x.TagSet)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.TagSetId })
            .HasPrincipalKey(s => new { s.SpaceId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Tag)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.TagId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // No duplicate tag on the same entity. (NULL TagId rows — FreeText —
        // are NULLs-distinct here; their cardinality is the partial index's job.)
        builder.HasIndex(x => new { x.TagSetId, x.EntityKind, x.EntityId, x.TagId })
            .IsUnique();

        // Cardinality: SingleSelect (1) and FreeText (2) sets allow at most ONE
        // application per (set, entity). set_type is denormalized from the
        // owning set precisely so this index can exist — a partial index
        // cannot consult tag_sets. MultiSelect (0) rows are exempt.
        builder.HasIndex(x => new { x.TagSetId, x.EntityKind, x.EntityId })
            .IsUnique()
            .HasFilter("set_type IN (1, 2)")
            .HasDatabaseName("ix_tag_applications_single_value_per_set");

        // "Tags of this entity" — the hot lookup for entity pages/editors.
        builder.HasIndex(x => new { x.EntityKind, x.EntityId });

        // "Entities carrying this tag" — filters, deploy-dialog target narrowing.
        builder.HasIndex(x => x.TagId);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

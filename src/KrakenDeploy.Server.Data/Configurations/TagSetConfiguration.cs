using KrakenDeploy.Server.Core.Domain.Tags;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for the Space-level <see cref="TagSet"/> (extended tag sets).
/// <c>Scopes</c> is a jsonb list (same idiom as the freeze scope lists) —
/// a set has at most a handful of scopes, so a join table would be noise.
/// </summary>
public class TagSetConfiguration : IEntityTypeConfiguration<TagSet>
{
    public void Configure(EntityTypeBuilder<TagSet> builder)
    {
        builder.ToTable("tag_sets");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.SortOrder).IsRequired();
        builder.Property(x => x.Type).IsRequired();

        builder.Property(x => x.Scopes)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<List<TaggableEntityKind>>())
            .IsRequired();

        // Space-level uniqueness (was (TenantId, Name) in the tenant-owned model).
        builder.HasIndex(x => new { x.SpaceId, x.Name }).IsUnique();

        // Composite Space FK: a tag can only belong to a tag set in its own Space.
        builder.HasMany(x => x.Tags)
            .WithOne(t => t.TagSet)
            .HasForeignKey(t => new { t.SpaceId, t.TagSetId })
            .HasPrincipalKey(s => new { s.SpaceId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

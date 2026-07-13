using KrakenDeploy.Server.Core.Domain.Tags;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="Tag"/> (renamed from the tenant-owned
/// <c>TenantTag</c>). Applications to entities live in
/// <c>tag_applications</c> — the old <c>target_tenant_tags</c> M2M is gone.
/// </summary>
public class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("tags");
        builder.HasKey(x => x.Id);

        // Child of TagSet — the composite FK (space_id, tag_set_id) lives in
        // TagSetConfiguration and transitively guarantees space_id.
        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        // Stores a CSS colour string. The UI color picker can emit rgb()/rgba()
        // (up to ~25 chars) as well as #hex, so size for the longest form.
        builder.Property(x => x.Color).HasMaxLength(32);
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.SortOrder).IsRequired();

        builder.HasIndex(x => new { x.TagSetId, x.Name }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

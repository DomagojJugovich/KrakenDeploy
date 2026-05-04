using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

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

        builder.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();

        builder.HasMany(x => x.Tags)
            .WithOne(t => t.TagSet)
            .HasForeignKey(t => t.TagSetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

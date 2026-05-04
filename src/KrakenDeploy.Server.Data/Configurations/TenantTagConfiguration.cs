using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class TenantTagConfiguration : IEntityTypeConfiguration<TenantTag>
{
    public void Configure(EntityTypeBuilder<TenantTag> builder)
    {
        builder.ToTable("tenant_tags");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Color).HasMaxLength(20);

        builder.HasIndex(x => new { x.TagSetId, x.Name }).IsUnique();

        // Target ↔ TenantTag many-to-many (implicit join table)
        builder.HasMany(x => x.Targets)
            .WithMany(t => t.TenantTags)
            .UsingEntity(j => j.ToTable("target_tenant_tags"));

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Slug).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.Slug).IsUnique();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);

        builder.Property(x => x.VariableSetId);

        builder.HasMany(x => x.TagSets)
            .WithOne(ts => ts.Tenant)
            .HasForeignKey(ts => ts.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Project ↔ Tenant many-to-many (implicit join table)
        builder.HasMany(x => x.Projects)
            .WithMany(p => p.Tenants)
            .UsingEntity(j => j.ToTable("project_tenants"));

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

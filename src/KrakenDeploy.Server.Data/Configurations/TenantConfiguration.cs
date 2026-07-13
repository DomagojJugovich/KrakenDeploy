using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Slug).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => new { x.SpaceId, x.Slug }).IsUnique();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);

        // Tenant-common variable set (reserved/dormant column). FK SET NULL so
        // deleting the set clears the pointer without deleting the tenant —
        // ownership stays tenant-side, not variable-set-side.
        builder.HasOne<VariableSet>()
            .WithMany()
            .HasForeignKey(x => x.VariableSetId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.VariableSetId);

        // Project ↔ Tenant many-to-many (implicit join table)
        builder.HasMany(x => x.Projects)
            .WithMany(p => p.Tenants)
            .UsingEntity(j => j.ToTable("project_tenants"));

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

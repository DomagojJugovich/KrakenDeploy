using KrakenDeploy.Server.Core.Domain.Projects;
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
        // ownership stays tenant-side, not variable-set-side. Composite Space FK
        // (raw-SQL column-list `ON DELETE SET NULL (variable_set_id)` in the
        // migration — EF Core 10 cannot emit the column subset for a composite FK
        // whose space_id is NOT NULL).
        builder.HasOne<VariableSet>()
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.VariableSetId })
            .HasPrincipalKey(vs => new { vs.SpaceId, vs.Id })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.VariableSetId);

        // Project ↔ Tenant many-to-many — explicit Space-scoped join: composite
        // FKs on BOTH sides pin project and tenant to the same Space.
        builder.HasMany(x => x.Projects)
            .WithMany(p => p.Tenants)
            .UsingEntity<ProjectTenant>(
                r => r.HasOne<Project>()
                    .WithMany()
                    .HasForeignKey(pt => new { pt.SpaceId, pt.ProjectId })
                    .HasPrincipalKey(p => new { p.SpaceId, p.Id })
                    .OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<Tenant>()
                    .WithMany()
                    .HasForeignKey(pt => new { pt.SpaceId, pt.TenantId })
                    .HasPrincipalKey(t => new { t.SpaceId, t.Id })
                    .OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("project_tenants");
                    j.HasKey(pt => new { pt.ProjectId, pt.TenantId });
                    j.Property(pt => pt.SpaceId).IsRequired();
                    j.HasIndex(pt => pt.TenantId);
                });

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

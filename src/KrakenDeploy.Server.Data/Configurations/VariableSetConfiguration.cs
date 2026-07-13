using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class VariableSetConfiguration : IEntityTypeConfiguration<VariableSet>
{
    public void Configure(EntityTypeBuilder<VariableSet> builder)
    {
        builder.ToTable("variable_sets");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        // Optional one-to-one: project sets carry ProjectId; library / tenant
        // sets leave it null. Composite Space FK: a project-kind set can only
        // point at a project in its own Space (VariableSet keeps its own direct
        // spaces FK as an aggregate root — project_id is optional so it is not an
        // owning parent).
        builder.HasOne(x => x.Project)
            .WithOne(p => p.VariableSet)
            .HasForeignKey<VariableSet>(x => new { x.SpaceId, x.ProjectId })
            .HasPrincipalKey<Project>(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired(false);

        // Uniqueness only over non-null project_id — many library/tenant sets
        // share NULL. Postgres filtered unique index.
        builder.HasIndex(x => x.ProjectId)
            .IsUnique()
            .HasFilter("project_id IS NOT NULL");

        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);

        // Library-set listing on the global page filters by (space, kind).
        builder.HasIndex(x => new { x.SpaceId, x.Kind });
    }
}

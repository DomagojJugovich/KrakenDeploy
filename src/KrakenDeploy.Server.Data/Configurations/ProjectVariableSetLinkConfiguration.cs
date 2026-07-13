using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class ProjectVariableSetLinkConfiguration : IEntityTypeConfiguration<ProjectVariableSetLink>
{
    public void Configure(EntityTypeBuilder<ProjectVariableSetLink> builder)
    {
        builder.ToTable("project_variable_set_links");
        builder.HasKey(x => new { x.ProjectId, x.VariableSetId });

        builder.Property(x => x.SpaceId).IsRequired();

        // Composite Space FKs on both ends: a project can only include a library
        // set from its own Space, and space_id is stamped on insert.
        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.ProjectId })
            .HasPrincipalKey(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<VariableSet>()
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.VariableSetId })
            .HasPrincipalKey(vs => new { vs.SpaceId, vs.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.VariableSetId);
    }
}

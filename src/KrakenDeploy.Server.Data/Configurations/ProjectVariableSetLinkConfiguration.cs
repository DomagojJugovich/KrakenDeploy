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

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<VariableSet>()
            .WithMany()
            .HasForeignKey(x => x.VariableSetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.VariableSetId);
    }
}

using KrakenDeploy.Server.Core.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class DeploymentProcessConfiguration : IEntityTypeConfiguration<DeploymentProcess>
{
    public void Configure(EntityTypeBuilder<DeploymentProcess> builder)
    {
        builder.ToTable("deployment_processes");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.HasOne(x => x.Project)
            .WithOne(p => p.Process)
            .HasForeignKey<DeploymentProcess>(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ProjectId).IsUnique();
    }
}

using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class DeploymentArtifactConfiguration : IEntityTypeConfiguration<DeploymentArtifact>
{
    public void Configure(EntityTypeBuilder<DeploymentArtifact> builder)
    {
        builder.ToTable("deployment_artifacts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SizeBytes).IsRequired();
        builder.Property(x => x.StoredPath).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.CollectedUtc).IsRequired();

        builder.HasOne(x => x.Deployment)
            .WithMany(d => d.Artifacts)
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Primary access pattern: list all artifacts for a deployment.
        builder.HasIndex(x => x.DeploymentId);
    }
}

using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class DeploymentOutputVariableConfiguration
    : IEntityTypeConfiguration<DeploymentOutputVariable>
{
    public void Configure(EntityTypeBuilder<DeploymentOutputVariable> builder)
    {
        builder.ToTable("deployment_output_variables");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Value).IsRequired();
        builder.Property(x => x.CapturedUtc).IsRequired();

        builder.HasOne(x => x.Deployment)
            .WithMany()
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Primary read pattern: "give me everything step X produced" (for the
        // deployment detail page) and "did step X produce variable Y?" (upsert).
        builder.HasIndex(x => new { x.DeploymentId, x.StepName });
        builder.HasIndex(x => new { x.DeploymentId, x.StepName, x.Name }).IsUnique();
    }
}

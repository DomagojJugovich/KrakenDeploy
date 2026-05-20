using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class DeploymentStepConfiguration : IEntityTypeConfiguration<DeploymentStep>
{
    public void Configure(EntityTypeBuilder<DeploymentStep> builder)
    {
        builder.ToTable("deployment_steps");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.StepType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.Property(x => x.PackageId).HasMaxLength(256).IsRequired();

        builder.Property(x => x.TargetRoles)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(x => x.Config)
            .HasJsonbColumn<Dictionary<string, string>>();

        // Phase D-6: nullable for the migration window (existing rows have no
        // pin); D-8 will fill in real pins once built-ins are package-backed.
        builder.Property(x => x.StepPackageName).HasMaxLength(128);
        builder.Property(x => x.StepPackageVersion).HasMaxLength(64);

        builder.HasOne(x => x.Process)
            .WithMany(p => p.Steps)
            .HasForeignKey(x => x.ProcessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProcessId, x.SortOrder });
    }
}

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

        // M14 step-execution knobs. Defaults preserve pre-M14 behaviour:
        // Success Condition, Required=true, no retries, no timeout, sequential.
        // The Required default is critical — existing rows backfilled by the
        // migration must read as "required" so a pre-M14 deployment process
        // doesn't silently change its failure semantics after the upgrade.
        builder.Property(x => x.Condition).HasDefaultValue(StepCondition.Success);
        builder.Property(x => x.ConditionVariableExpression).HasMaxLength(1024);
        builder.Property(x => x.Required).HasDefaultValue(true);
        builder.Property(x => x.MaxRetries).HasDefaultValue(0);
        builder.Property(x => x.RetryDelaySeconds).HasDefaultValue(0);
        builder.Property(x => x.TimeoutSeconds).HasDefaultValue(0);
        builder.Property(x => x.StartTrigger).HasDefaultValue(StepStartTrigger.StartAfterPrevious);

        builder.HasOne(x => x.Process)
            .WithMany(p => p.Steps)
            .HasForeignKey(x => x.ProcessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProcessId, x.SortOrder });

        // M15 — self-FK for parent/child step composition. ON DELETE
        // CASCADE so deleting a Step Group removes its children atomically.
        // Filtered index (parent_step_id IS NOT NULL) keeps top-level
        // steps out of the lookup since most are flat.
        builder.HasOne(x => x.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(x => x.ParentStepId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ParentStepId)
            .HasFilter("parent_step_id IS NOT NULL");
    }
}

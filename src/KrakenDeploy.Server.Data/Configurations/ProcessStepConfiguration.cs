using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for the unified <see cref="ProcessStep"/> (<c>process_steps</c>) —
/// replaces <c>deployment_steps</c> + <c>runbook_steps</c>. Carries the FULL
/// execution-knob set for both owner kinds; <c>target_roles</c> is <c>text[]</c>
/// (runbook steps used jsonb) and lengths are unified: name 256 / step_type 128 /
/// package_id 256.
/// </summary>
public class ProcessStepConfiguration : IEntityTypeConfiguration<ProcessStep>
{
    public void Configure(EntityTypeBuilder<ProcessStep> builder)
    {
        builder.ToTable("process_steps");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.StepType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SortOrder).IsRequired();
        builder.Property(x => x.PackageId).HasMaxLength(256).IsRequired();

        builder.Property(x => x.TargetRoles)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(x => x.Config)
            .HasJsonbColumn<Dictionary<string, string>>();

        builder.Property(x => x.StepPackageName).HasMaxLength(128);
        builder.Property(x => x.StepPackageVersion).HasMaxLength(64);

        // M14 execution knobs. Defaults preserve pre-M14 behaviour (Success
        // Condition, Required=true, no retries, no timeout, sequential).
        builder.Property(x => x.Condition).HasDefaultValue(StepCondition.Success);
        builder.Property(x => x.ConditionVariableExpression).HasMaxLength(1024);
        builder.Property(x => x.Required).HasDefaultValue(true);
        builder.Property(x => x.MaxRetries).HasDefaultValue(0);
        builder.Property(x => x.RetryDelaySeconds).HasDefaultValue(0);
        builder.Property(x => x.TimeoutSeconds).HasDefaultValue(0);
        builder.Property(x => x.StartTrigger).HasDefaultValue(StepStartTrigger.StartAfterPrevious);

        // D3 control-flow flags promoted from jsonb Config. Defaults preserve
        // pre-D3 behaviour (agent-side execution, no rolling cap, no ForEach).
        builder.Property(x => x.RunOnServer).HasDefaultValue(false);
        builder.Property(x => x.MaxParallelism); // nullable int, no default
        builder.Property(x => x.ForEachCollection).HasMaxLength(512);
        builder.Property(x => x.ForEachParallel).HasDefaultValue(false);

        // Composite Space FK: a step can only belong to a process in its own Space.
        builder.HasOne(x => x.Process)
            .WithMany(p => p.Steps)
            .HasForeignKey(x => new { x.SpaceId, x.ProcessId })
            .HasPrincipalKey(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProcessId, x.SortOrder });

        // M15 — self-FK for parent/child step composition. ON DELETE CASCADE so
        // deleting a Step Group removes its children atomically. Composite so a
        // step's parent must live in the same Space.
        builder.HasOne(x => x.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(x => new { x.SpaceId, x.ParentStepId })
            .HasPrincipalKey(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ParentStepId)
            .HasFilter("parent_step_id IS NOT NULL");
    }
}

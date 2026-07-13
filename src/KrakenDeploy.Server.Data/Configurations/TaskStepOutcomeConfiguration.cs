using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for <see cref="TaskStepOutcome"/> (<c>task_step_outcomes</c>). Surrogate
/// <c>Id</c> PK plus a unique natural key (TaskId, StepIndex, TargetId) so the
/// orchestrator + agent can upsert per (task, step, target).
/// </summary>
public sealed class TaskStepOutcomeConfiguration : IEntityTypeConfiguration<TaskStepOutcome>
{
    public void Configure(EntityTypeBuilder<TaskStepOutcome> builder)
    {
        builder.ToTable("task_step_outcomes");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.StepIndex).IsRequired();
        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Outcome).HasConversion<int>().IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.ErrorMessage);
        builder.Property(x => x.StartedUtc);
        builder.Property(x => x.CompletedUtc).IsRequired();
        builder.Property(x => x.IsServerSide).IsRequired();
        builder.Property(x => x.Required).IsRequired();
        builder.Property(x => x.TargetId);

        // Real FK RESTRICT — execution history pins its targets; deletion goes
        // through the archived-flag escape hatch, never by orphaning history rows.
        // Composite so the target must be in the outcome's Space (target_id is
        // nullable → the composite FK is simply not enforced for server-once steps).
        builder.HasOne<Core.Domain.Targets.DeploymentTarget>()
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.TargetId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Composite Space FK: an outcome can only belong to a task in its Space.
        builder.HasOne(x => x.Task)
            .WithMany(t => t.StepOutcomes)
            .HasForeignKey(x => new { x.SpaceId, x.TaskId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Primary read: every step's outcome for a task, in step order; also the
        // upsert-by-natural-key path. NULLS NOT DISTINCT so a server-once step's
        // NULL TargetId is treated as a single logical key — matching the
        // worker's upsert, which already dedupes NULL==NULL via EF null-semantics;
        // this closes the concurrent-duplicate-insert gap the DB previously allowed.
        builder.HasIndex(x => new { x.TaskId, x.StepIndex, x.TargetId })
            .IsUnique()
            .AreNullsDistinct(false);
    }
}

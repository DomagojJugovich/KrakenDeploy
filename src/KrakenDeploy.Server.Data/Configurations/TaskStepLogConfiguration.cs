using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for the compacted (blob) half of the hybrid log model,
/// <see cref="TaskStepLog"/> (<c>task_step_logs</c>). One row per (task, step,
/// target); <c>content</c> is a TOAST/lz4-compressed text blob. NOT ISpaceScoped —
/// scope inherits via the task. NO trgm/GIN index over content (global text search
/// is the out-of-band Seq pipeline).
/// </summary>
public sealed class TaskStepLogConfiguration : IEntityTypeConfiguration<TaskStepLog>
{
    public void Configure(EntityTypeBuilder<TaskStepLog> builder)
    {
        builder.ToTable("task_step_logs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepIndex).IsRequired();
        builder.Property(x => x.TargetId);
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.LineCount).IsRequired();
        builder.Property(x => x.ErrorCount).IsRequired();
        builder.Property(x => x.WarnCount).IsRequired();
        builder.Property(x => x.FirstErrorLine);
        builder.Property(x => x.ByteSize).IsRequired();
        builder.Property(x => x.CompletedUtc).IsRequired();

        builder.HasOne(x => x.Task)
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        // Primary read: stitch a task's completed step blobs in (step, target) order.
        builder.HasIndex(x => new { x.TaskId, x.StepIndex, x.TargetId });
    }
}

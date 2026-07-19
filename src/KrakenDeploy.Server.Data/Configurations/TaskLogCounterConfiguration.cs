using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for the per-task log-sequence counter <see cref="TaskLogCounter"/>
/// (<c>task_log_counters</c>). PK is <c>task_id</c> (one row per task) so the
/// DB-atomic allocator can <c>INSERT … ON CONFLICT (task_id) DO UPDATE</c>. Cascades
/// with the task. NOT ISpaceScoped — scope inherits via the task, like the other
/// log tables.
/// </summary>
public sealed class TaskLogCounterConfiguration : IEntityTypeConfiguration<TaskLogCounter>
{
    public void Configure(EntityTypeBuilder<TaskLogCounter> builder)
    {
        builder.ToTable("task_log_counters");

        // One row per task — the task id IS the key, which also gives the
        // allocator's ON CONFLICT (task_id) its arbiter.
        builder.HasKey(x => x.TaskId);

        builder.Property(x => x.NextSequence).IsRequired();

        builder.HasOne(x => x.Task)
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

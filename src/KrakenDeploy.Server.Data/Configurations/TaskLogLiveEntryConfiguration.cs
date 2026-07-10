using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for the staging half of the hybrid log model,
/// <see cref="TaskLogLiveEntry"/> (<c>task_log_live</c>). NOT ISpaceScoped — scope
/// inherits via the task. Unique (TaskId, Sequence) serves the ordered live tail
/// and guards the DB-atomic sequencer against collisions.
/// </summary>
public sealed class TaskLogLiveEntryConfiguration : IEntityTypeConfiguration<TaskLogLiveEntry>
{
    public void Configure(EntityTypeBuilder<TaskLogLiveEntry> builder)
    {
        builder.ToTable("task_log_live");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepIndex).IsRequired();
        builder.Property(x => x.TargetId);
        builder.Property(x => x.Sequence).IsRequired();
        builder.Property(x => x.Level).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Timestamp).IsRequired();
        builder.Property(x => x.Message).IsRequired();

        builder.HasOne(x => x.Task)
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ordered live tail + collision guard for the sequencer.
        builder.HasIndex(x => new { x.TaskId, x.Sequence }).IsUnique();
    }
}

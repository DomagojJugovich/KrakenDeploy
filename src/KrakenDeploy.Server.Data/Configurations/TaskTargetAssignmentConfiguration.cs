using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for the task↔target join <see cref="TaskTargetAssignment"/>
/// (<c>task_target_assignments</c>). Composite PK (TaskId, TargetId). Task-side FK
/// cascades; target-side is RESTRICT (deleting a machine must not rip out historical
/// assignments). NOT ISpaceScoped — scope inherits via the task.
/// </summary>
public sealed class TaskTargetAssignmentConfiguration
    : IEntityTypeConfiguration<TaskTargetAssignment>
{
    public void Configure(EntityTypeBuilder<TaskTargetAssignment> builder)
    {
        builder.ToTable("task_target_assignments");

        builder.HasKey(x => new { x.TaskId, x.TargetId });

        builder.Property(x => x.AddedUtc).IsRequired();

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Targets)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Target)
            .WithMany()
            .HasForeignKey(x => x.TargetId)
            .OnDelete(DeleteBehavior.Restrict);

        // Reverse lookup: "which tasks hit target X?"
        builder.HasIndex(x => x.TargetId);
    }
}

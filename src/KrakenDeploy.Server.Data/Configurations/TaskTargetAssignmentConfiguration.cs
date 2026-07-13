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

        builder.Property(x => x.SpaceId).IsRequired();
        builder.Property(x => x.AddedUtc).IsRequired();

        // Composite Space FKs on both ends: a task can only be assigned to a
        // target in its own Space, and space_id is stamped on insert.
        builder.HasOne(x => x.Task)
            .WithMany(t => t.Targets)
            .HasForeignKey(x => new { x.SpaceId, x.TaskId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Target)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.TargetId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Reverse lookup: "which tasks hit target X?"
        builder.HasIndex(x => x.TargetId);
    }
}

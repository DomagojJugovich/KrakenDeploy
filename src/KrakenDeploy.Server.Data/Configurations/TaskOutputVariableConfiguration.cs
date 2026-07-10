using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>Mapping for <see cref="TaskOutputVariable"/> (<c>task_output_variables</c>).</summary>
public sealed class TaskOutputVariableConfiguration : IEntityTypeConfiguration<TaskOutputVariable>
{
    public void Configure(EntityTypeBuilder<TaskOutputVariable> builder)
    {
        builder.ToTable("task_output_variables");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Value).IsRequired();
        builder.Property(x => x.CapturedUtc).IsRequired();

        builder.HasOne(x => x.Task)
            .WithMany(t => t.OutputVariables)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        // Read "everything step X produced" + upsert "did step X produce Y?".
        builder.HasIndex(x => new { x.TaskId, x.StepName });
        builder.HasIndex(x => new { x.TaskId, x.StepName, x.Name }).IsUnique();
    }
}

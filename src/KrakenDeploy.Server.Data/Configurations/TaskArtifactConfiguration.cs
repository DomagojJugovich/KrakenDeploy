using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>Mapping for <see cref="TaskArtifact"/> (<c>task_artifacts</c>).</summary>
public sealed class TaskArtifactConfiguration : IEntityTypeConfiguration<TaskArtifact>
{
    public void Configure(EntityTypeBuilder<TaskArtifact> builder)
    {
        builder.ToTable("task_artifacts");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SizeBytes).IsRequired();
        builder.Property(x => x.StoredPath).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.CollectedUtc).IsRequired();

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Artifacts)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.TaskId);
    }
}

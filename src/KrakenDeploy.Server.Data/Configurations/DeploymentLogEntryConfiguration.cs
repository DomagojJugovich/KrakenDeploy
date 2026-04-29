using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class DeploymentLogEntryConfiguration : IEntityTypeConfiguration<DeploymentLogEntry>
{
    public void Configure(EntityTypeBuilder<DeploymentLogEntry> builder)
    {
        builder.ToTable("deployment_log_entries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Sequence).IsRequired();
        builder.Property(x => x.Timestamp).IsRequired();
        builder.Property(x => x.Message).IsRequired();
        builder.Property(x => x.Level).HasMaxLength(16).IsRequired();

        builder.HasOne(x => x.Deployment)
            .WithMany(d => d.LogEntries)
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Fetching a deployment's log in order is the primary read pattern.
        builder.HasIndex(x => new { x.DeploymentId, x.Sequence });
    }
}

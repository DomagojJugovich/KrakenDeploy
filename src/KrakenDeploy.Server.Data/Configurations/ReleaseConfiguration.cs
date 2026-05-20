using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.ToTable("releases");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Version).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ReleaseNotes);

        // Snapshot of the deployment process at release creation time — stored as jsonb.
        builder.Property(x => x.ProcessSnapshot)
            .HasJsonbColumn<List<StepSnapshot>>();

        // Variable snapshot (Octopus-style "Update Variables" feature).
        // VariableSnapshotUpdatedUtc IS NULL is the "release predates feature"
        // signal — DeploymentWorker falls back to live project-variable
        // resolution in that case. Empty-list + non-null timestamp means the
        // user explicitly snapshotted-empty.
        builder.Property(x => x.VariableSnapshot)
            .HasJsonbColumn<List<VariableSnapshot>>();
        builder.Property(x => x.VariableSnapshotUpdatedUtc);

        builder.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => x.ChannelId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

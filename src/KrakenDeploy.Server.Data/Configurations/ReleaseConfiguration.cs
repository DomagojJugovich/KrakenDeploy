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

        builder.ConfigureSpaceScopeAsChild();

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

        // Composite Space FK: a release can only belong to a project in its Space.
        builder.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.ProjectId })
            .HasPrincipalKey(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Composite Space FK to the (optional) channel. SetNull on a composite FK
        // whose space_id is NOT NULL requires the Postgres 15+ column-list form
        // `ON DELETE SET NULL (channel_id)`, which EF Core 10 cannot emit — the
        // migration rewrites this constraint with raw SQL. EF keeps modelling it
        // as SetNull, which is harmless (the column subset is invisible to the
        // model comparison, so no snapshot drift).
        builder.HasOne(x => x.Channel)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.ChannelId })
            .HasPrincipalKey(c => new { c.SpaceId, c.Id })
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

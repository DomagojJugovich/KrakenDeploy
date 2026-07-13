using KrakenDeploy.Server.Core.Domain.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("channels");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.IsDefault).IsRequired();
        builder.Property(x => x.VersionRange).HasMaxLength(200);
        builder.Property(x => x.VersionTag).HasMaxLength(100);

        // Composite Space FK: a channel can only belong to a project in its Space.
        builder.HasOne(x => x.Project)
            .WithMany(p => p.Channels)
            .HasForeignKey(x => new { x.SpaceId, x.ProjectId })
            .HasPrincipalKey(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT — deleting a lifecycle that gates a channel must fail
        // loudly, not silently null the pointer and un-gate deploys. Composite
        // so the lifecycle must be in the channel's Space.
        builder.HasOne(x => x.Lifecycle)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.LifecycleId })
            .HasPrincipalKey(l => new { l.SpaceId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Unique channel name per project.
        builder.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();

        // At most one default channel per project — filtered unique index
        // (precedent: project_groups one-default-per-Space). Replaces the
        // non-transactional clear-then-set invariant ChannelService relied on.
        builder.HasIndex(x => x.ProjectId)
            .IsUnique()
            .HasFilter("is_default");

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

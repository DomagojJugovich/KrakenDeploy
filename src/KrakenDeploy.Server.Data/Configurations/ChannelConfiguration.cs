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

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.IsDefault).IsRequired();
        builder.Property(x => x.VersionRange).HasMaxLength(200);
        builder.Property(x => x.VersionTag).HasMaxLength(100);

        builder.HasOne(x => x.Project)
            .WithMany(p => p.Channels)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Lifecycle)
            .WithMany()
            .HasForeignKey(x => x.LifecycleId)
            .OnDelete(DeleteBehavior.SetNull);

        // Unique channel name per project.
        builder.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

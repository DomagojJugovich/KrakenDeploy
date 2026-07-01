using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.ControlPlane.Catalog.Configurations;

public class ShardConfiguration : IEntityTypeConfiguration<Shard>
{
    public void Configure(EntityTypeBuilder<Shard> builder)
    {
        builder.ToTable("shards");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.HostSecretRef).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Capacity).IsRequired();

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);
    }
}

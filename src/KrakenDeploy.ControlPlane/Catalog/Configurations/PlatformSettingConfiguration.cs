using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.ControlPlane.Catalog.Configurations;

public class PlatformSettingConfiguration : IEntityTypeConfiguration<PlatformSetting>
{
    public void Configure(EntityTypeBuilder<PlatformSetting> builder)
    {
        builder.ToTable("platform_settings");
        builder.HasKey(x => x.Key);

        builder.Property(x => x.Key).HasMaxLength(100);
        builder.Property(x => x.Value).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.ModifiedUtc).IsRequired();
    }
}

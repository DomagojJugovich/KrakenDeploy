using KrakenDeploy.Server.Core.Domain.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class MaintenanceSettingsConfiguration
    : IEntityTypeConfiguration<MaintenanceSettings>
{
    public void Configure(EntityTypeBuilder<MaintenanceSettings> builder)
    {
        builder.ToTable("maintenance_settings");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Reason).HasMaxLength(2000);
    }
}

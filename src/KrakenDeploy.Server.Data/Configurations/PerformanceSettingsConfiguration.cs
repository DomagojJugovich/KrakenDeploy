using KrakenDeploy.Server.Core.Domain.Performance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class PerformanceSettingsConfiguration
    : IEntityTypeConfiguration<PerformanceSettings>
{
    public void Configure(EntityTypeBuilder<PerformanceSettings> builder)
    {
        builder.ToTable("performance_settings");
        builder.HasKey(p => p.Id);
        // All knobs are simple primitives — no special column mapping, except
        // EmbedOfflineRunner carries a DB default so the column backfills true
        // on the pre-existing singleton row when the migration adds it.
        builder.Property(p => p.EmbedOfflineRunner)
            .HasDefaultValue(PerformanceSettings.DefaultEmbedOfflineRunner);
    }
}

using KrakenDeploy.Server.Core.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class BackupSettingsConfiguration : IEntityTypeConfiguration<BackupSettings>
{
    public void Configure(EntityTypeBuilder<BackupSettings> builder)
    {
        builder.ToTable("backup_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.TargetDirectory).IsRequired().HasMaxLength(500);
        builder.Property(s => s.ScheduleCron).HasMaxLength(64);
    }
}

public sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> builder)
    {
        builder.ToTable("backup_runs");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.BundlePath).HasMaxLength(2000);
        builder.Property(r => r.TriggeredBy).IsRequired().HasMaxLength(32);
        builder.Property(r => r.ErrorMessage).HasMaxLength(4000);
        builder.Property(r => r.Outcome).HasConversion<int>();

        // Index on StartedUtc desc so the dashboard "last N runs" query
        // pulls cheaply via the index.
        builder.HasIndex(r => r.StartedUtc).IsDescending();
    }
}

using KrakenDeploy.Server.Core.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

// BackupSettings is now a System-scoped ISettingsDocument in the unified
// `settings` table (see SettingConfiguration); only backup_runs remains a table.
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

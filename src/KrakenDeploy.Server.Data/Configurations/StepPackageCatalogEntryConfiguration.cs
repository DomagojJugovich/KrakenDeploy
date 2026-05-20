using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class StepPackageCatalogEntryConfiguration
    : IEntityTypeConfiguration<StepPackageCatalogEntry>
{
    public void Configure(EntityTypeBuilder<StepPackageCatalogEntry> builder)
    {
        builder.ToTable("step_package_catalog");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Version).IsRequired().HasMaxLength(64);
        // Catalog rows are upserted by (name, version) on each refresh.
        builder.HasIndex(x => new { x.Name, x.Version }).IsUnique();
        // Secondary index for "all versions of X" + "is X installed?" lookups.
        builder.HasIndex(x => x.Name);

        builder.Property(x => x.DownloadUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.Sha256).IsRequired().HasMaxLength(64);
        builder.Property(x => x.ReleaseHtmlUrl).IsRequired().HasMaxLength(2048);

        builder.Property(x => x.ManifestJson).IsRequired().HasColumnType("jsonb");
        builder.Property(x => x.Changelog).HasColumnType("text");

        builder.Property(x => x.PublishedUtc).IsRequired();
        builder.Property(x => x.LastSyncedUtc).IsRequired();
    }
}

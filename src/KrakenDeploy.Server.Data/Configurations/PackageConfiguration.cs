using KrakenDeploy.Server.Core.Domain.Packages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class PackageConfiguration : IEntityTypeConfiguration<Package>
{
    public void Configure(EntityTypeBuilder<Package> builder)
    {
        builder.ToTable("packages");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.PackageId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.StoredPath).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.SizeBytes).IsRequired();
        builder.Property(x => x.UploadedUtc).IsRequired();

        // Each (packageId, version) pair must be unique within a Space.
        builder.HasIndex(x => new { x.SpaceId, x.PackageId, x.Version }).IsUnique();
        builder.HasIndex(x => new { x.SpaceId, x.PackageId });
    }
}

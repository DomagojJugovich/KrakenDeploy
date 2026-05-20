using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class StepPackageConfiguration : IEntityTypeConfiguration<StepPackage>
{
    public void Configure(EntityTypeBuilder<StepPackage> builder)
    {
        builder.ToTable("step_packages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Version).IsRequired().HasMaxLength(64);
        builder.HasIndex(x => new { x.Name, x.Version }).IsUnique();
        // Secondary index for "what's the latest of X?" lookups.
        builder.HasIndex(x => x.Name);

        builder.Property(x => x.Sha256).IsRequired().HasMaxLength(64);

        builder.Property(x => x.ManifestJson).IsRequired().HasColumnType("jsonb");
        builder.Property(x => x.UiSchemaJson).HasColumnType("jsonb");

        builder.Property(x => x.Source).IsRequired().HasConversion<int>();

        builder.Property(x => x.StepTypes).IsRequired().HasMaxLength(500);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

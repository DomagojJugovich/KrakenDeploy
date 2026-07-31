using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class StepTypeEntryConfiguration : IEntityTypeConfiguration<StepTypeEntry>
{
    public void Configure(EntityTypeBuilder<StepTypeEntry> builder)
    {
        builder.ToTable("step_types");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TypeId).IsRequired().HasMaxLength(200);
        builder.HasIndex(x => x.TypeId).IsUnique();

        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Category).HasMaxLength(100);
        builder.Property(x => x.Description).HasMaxLength(1000);

        builder.Property(x => x.ExecutionLocus).IsRequired().HasConversion<int>();
        builder.Property(x => x.Source).IsRequired().HasConversion<int>();

        builder.Property(x => x.ServingPackageName).HasMaxLength(200);
        builder.Property(x => x.ServingPackageVersion).HasMaxLength(64);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

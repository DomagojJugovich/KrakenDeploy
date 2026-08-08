using KrakenDeploy.Server.Core.Domain.StepPackages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class StepPackageSchemaConfiguration : IEntityTypeConfiguration<StepPackageSchema>
{
    public void Configure(EntityTypeBuilder<StepPackageSchema> builder)
    {
        builder.ToTable("step_package_schemas");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepType).IsRequired().HasMaxLength(200);
        builder.Property(x => x.SchemaJson).IsRequired().HasColumnType("jsonb");

        // Rows die with their (name, version) package row.
        builder.HasOne<StepPackage>()
               .WithMany()
               .HasForeignKey(x => x.StepPackageId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.StepPackageId, x.StepType }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

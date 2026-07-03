using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.ControlPlane.Catalog.Configurations;

public class AppReleaseConfiguration : IEntityTypeConfiguration<AppRelease>
{
    public void Configure(EntityTypeBuilder<AppRelease> builder)
    {
        builder.ToTable("app_releases");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasMaxLength(100);
        builder.Property(x => x.Label).HasMaxLength(200).IsRequired();

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);

        builder.Property(x => x.DeployedAtUtc).IsRequired();

        // Invariant (runbook step 0): a slot hosts at most ONE non-Retired release.
        // Enforced at the DB so a mis-sequenced register can never corrupt routing.
        // (3 = AppReleaseStatus.Retired, int-converted.)
        builder.HasIndex(x => x.SlotNo)
            .IsUnique()
            .HasFilter("status <> 3")
            .HasDatabaseName("ux_app_releases_slot_no_live");
    }
}

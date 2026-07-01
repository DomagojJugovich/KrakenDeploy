using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.ControlPlane.Catalog.Configurations;

public class BusinessAccountConfiguration : IEntityTypeConfiguration<BusinessAccount>
{
    public void Configure(EntityTypeBuilder<BusinessAccount> builder)
    {
        builder.ToTable("business_accounts");
        builder.HasKey(x => x.Id);

        // DNS label max is 63 chars; subdomain is normalized lower-case.
        builder.Property(x => x.Subdomain).HasMaxLength(63).IsRequired();
        builder.HasIndex(x => x.Subdomain).IsUnique();

        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);

        builder.Property(x => x.Tier).IsRequired().HasConversion<int>();

        builder.Property(x => x.ConnSecretRef).HasMaxLength(512).IsRequired();

        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.ModifiedUtc).IsRequired();

        builder.HasOne(x => x.Shard)
            .WithMany(s => s.Accounts)
            .HasForeignKey(x => x.ShardId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="ApiKey"/>. Platform-level table (no Space
/// query filter — the entity is deliberately not <c>ISpaceScoped</c>;
/// <c>SpaceId</c> is an optional restriction column, mirroring
/// <c>AuditEntry</c>'s never-filtered nullable tag).
/// </summary>
public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(x => x.Id);

        // The auth handler's hot-path lookup: recomputed SHA-256 → row.
        builder.HasIndex(x => x.KeyHash).IsUnique();

        // "My keys" listing.
        builder.HasIndex(x => x.UserId);

        // One purpose label per owner keeps the list navigable.
        builder.HasIndex(x => new { x.UserId, x.Name }).IsUnique();

        // Keys authenticate AS the owning user, so they die with the account.
        // Real FK CASCADE (belt-and-braces cleanup still lives in
        // UserService.DeleteAsync). No navigation on the domain entity — the
        // house convention keeps domain->Identity refs as bare Guids.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
        builder.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
    }
}

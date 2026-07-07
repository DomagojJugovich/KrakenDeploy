using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="DataEncryptionKey"/>. Platform-level table (no
/// Space query filter — not <c>ISpaceScoped</c>).
/// </summary>
public sealed class DataEncryptionKeyConfiguration : IEntityTypeConfiguration<DataEncryptionKey>
{
    public void Configure(EntityTypeBuilder<DataEncryptionKey> builder)
    {
        builder.ToTable("data_encryption_keys");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.WrappedDek).IsRequired().HasMaxLength(2048);

        // Unique on account_id with NULLS NOT DISTINCT — this is what actually
        // enforces "at most ONE instance-wide DEK": a plain partial unique index
        // would NOT, because Postgres treats NULLs as distinct, so two rows with
        // account_id = NULL would both be allowed (defeating the concurrent-boot
        // race guard in DekProvider.EnsureDekAsync). Collapsing NULLs caps the
        // instance-wide row at one; non-null (future per-account) rows stay unique.
        builder.HasIndex(x => x.AccountId)
            .IsUnique()
            .AreNullsDistinct(false);
    }
}

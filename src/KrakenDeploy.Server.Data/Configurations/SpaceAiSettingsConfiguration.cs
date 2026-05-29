using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF config for <see cref="SpaceAiSettings"/> (Phase M11.A.6.1).
/// One row per Space enforced by a unique index on
/// <see cref="SpaceAiSettings.SpaceId"/>. Length caps keep the settings
/// table compact — model + base-url are bounded values.
/// </summary>
public class SpaceAiSettingsConfiguration : IEntityTypeConfiguration<SpaceAiSettings>
{
    public void Configure(EntityTypeBuilder<SpaceAiSettings> builder)
    {
        builder.ToTable("space_ai_settings");
        builder.HasKey(x => x.Id);

        // 1-to-1 with Space: enforced at the DB level so a bug that tries
        // to create a second row fails loudly instead of silently
        // shadowing the first.
        builder.HasIndex(x => x.SpaceId).IsUnique();

        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Model).HasMaxLength(128);
        builder.Property(x => x.BaseUrl).HasMaxLength(512);

        // ApiKeyEncrypted is the base64 ciphertext + nonce + tag produced
        // by IEncryptionService. AES-256-GCM output for a typical
        // 50-char API key fits easily in 256 chars; allow more headroom
        // for OAuth-style multi-part tokens.
        builder.Property(x => x.ApiKeyEncrypted).HasMaxLength(2048);

        // numeric(12,6) — same precision as AiCallLog.CostUsd. Budgets
        // shouldn't carry sub-cent precision but the type matches so
        // arithmetic doesn't round-trip awkwardly.
        builder.Property(x => x.BudgetUsdPerMonth).HasColumnType("numeric(12, 6)");

        // M11.E ad-hoc iteration cap. DB default backfills pre-existing rows
        // to 5 so the column can be NOT NULL without a data migration.
        builder.Property(x => x.AdhocMaxIterations).HasDefaultValue(5);
    }
}

using KrakenDeploy.Server.Core.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for the unified <see cref="Setting"/> table. Platform-level (no
/// Space query filter — the scope discriminator is nullable and scoping lives in
/// <c>SettingsService</c>).
/// </summary>
public sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.ToTable("settings");
        builder.HasKey(x => x.Id);

        // Scope discriminator stored as smallint (0 = System, 1 = Space, 2 = User).
        builder.Property(x => x.ScopeType).HasConversion<short>();
        builder.Property(x => x.Key).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();

        // One document per (scope_type, scope_id, key). NULLS NOT DISTINCT so the
        // nullable scope_id of System documents still caps at one row per key
        // (Postgres treats NULLs as distinct by default, which would let two
        // System rows for the same key coexist) — the data_encryption_keys idiom.
        builder.HasIndex(x => new { x.ScopeType, x.ScopeId, x.Key })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_settings_scope_key");

        // Optimistic concurrency via the PostgreSQL system column xmin — makes the
        // read-modify-write on the multi-key feature-flags overrides document
        // race-safe (a lost update surfaces as DbUpdateConcurrencyException, which
        // SettingsService.MutateAsync retries against a fresh read). Npgsql 10
        // dropped the UseXminAsConcurrencyToken() helper; a `uint` row-version
        // shadow property mapped to the xmin system column is the current idiom
        // (NpgsqlPostgresModelFinalizingConvention recognises it — no DDL is
        // emitted for the system column).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();
    }
}

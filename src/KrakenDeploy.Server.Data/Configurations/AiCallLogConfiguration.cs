using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="AiCallLog"/> (Phase M11.A.3).
/// Pins the column types + indexes — the wrapper writes thousands of rows
/// per Space per day in active use, so the indexes are doing meaningful
/// work behind the budget + usage rollups.
/// </summary>
public class AiCallLogConfiguration : IEntityTypeConfiguration<AiCallLog>
{
    public void Configure(EntityTypeBuilder<AiCallLog> builder)
    {
        builder.ToTable("ai_call_logs");
        builder.HasKey(x => x.Id);

        // FK to spaces; both composite indexes below lead with space_id, so
        // no standalone space_id index.
        builder.ConfigureSpaceScope(addSpaceIdIndex: false);

        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Model).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Feature).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(4096);
        builder.Property(x => x.CorrelationId).HasMaxLength(128);
        builder.Property(x => x.ScrubbedVariableNames).HasMaxLength(2048);

        // PromptBodyJson is jsonb so per-Space body-search queries can use
        // Postgres jsonb operators directly. ResponseBody stays text — it's
        // free-form prose, not structured.
        builder.Property(x => x.PromptBodyJson).HasColumnType("jsonb");
        builder.Property(x => x.ResponseBody).HasColumnType("text");

        // CostUsd: store with 6 fractional decimals so we keep fractional
        // cent precision for cheap models — a $0.001 / 1k-token model can
        // bill a single call at $0.000300 and we don't round to zero.
        builder.Property(x => x.CostUsd).HasColumnType("numeric(12, 6)");

        // Two indexes for the common access patterns:
        //   1. "last N hours of AI calls for this Space" — usage panel.
        //   2. "last N hours of AI calls for this Space, this feature" —
        //      per-feature rollups for the budget UI.
        builder.HasIndex(x => new { x.SpaceId, x.CreatedUtc });
        builder.HasIndex(x => new { x.SpaceId, x.Feature, x.CreatedUtc });
    }
}

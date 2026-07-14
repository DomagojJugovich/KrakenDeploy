using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="AdhocIteration"/> (M11.E.12). One row per turn;
/// <c>(session_id, iter_number)</c> is unique. The generated script and the
/// per-target results are unbounded text / jsonb — bounded in practice by the
/// generation + result sizes, not by a column limit.
/// </summary>
public sealed class AdhocIterationConfiguration : IEntityTypeConfiguration<AdhocIteration>
{
    public void Configure(EntityTypeBuilder<AdhocIteration> builder)
    {
        builder.ToTable("adhoc_iterations");
        builder.HasKey(i => i.Id);

        // Child of AdhocSession — the composite FK (space_id, session_id) lives in
        // AdhocSessionConfiguration and transitively guarantees space_id.
        builder.ConfigureSpaceScopeAsChild();

        builder.HasIndex(i => new { i.SessionId, i.IterNumber }).IsUnique();

        builder.Property(i => i.GeneratedScript).IsRequired();
        builder.Property(i => i.Description).HasMaxLength(2000);
        builder.Property(i => i.RiskAssessment).HasMaxLength(2000);
        builder.Property(i => i.ExpectedOutputShape).HasMaxLength(2000);
        builder.Property(i => i.Status).HasConversion<int>();
        builder.Property(i => i.Verdict).HasConversion<int>();
        builder.Property(i => i.Narrative).HasMaxLength(4000);
        builder.Property(i => i.ScriptSignature).HasMaxLength(1024);
        builder.Property(i => i.ApprovedByDisplay).HasMaxLength(256);
        builder.Property(i => i.FirstApprovedByDisplay).HasMaxLength(256);
        builder.Property(i => i.ResultsJson).HasColumnType("jsonb").IsRequired();
    }
}

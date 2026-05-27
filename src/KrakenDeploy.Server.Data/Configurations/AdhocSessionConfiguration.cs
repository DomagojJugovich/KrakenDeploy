using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="AdhocSession"/> (M11.E.12). The frozen target
/// set lives in a jsonb column so the session stays one self-contained row —
/// it's a write-once immutable blob the dispatcher reads, never a queried
/// relation. Iterations cascade-delete with the session.
/// </summary>
public sealed class AdhocSessionConfiguration : IEntityTypeConfiguration<AdhocSession>
{
    public void Configure(EntityTypeBuilder<AdhocSession> builder)
    {
        builder.ToTable("adhoc_sessions");
        builder.HasKey(s => s.Id);

        // The /adhoc page lists a Space's sessions newest-first.
        builder.HasIndex(s => new { s.SpaceId, s.CreatedUtc });

        builder.Property(s => s.Prompt).IsRequired().HasMaxLength(8000);
        builder.Property(s => s.Mode).HasConversion<int>();
        builder.Property(s => s.Status).HasConversion<int>();
        builder.Property(s => s.FrozenTargetSetJson).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.CreatedByDisplay).HasMaxLength(256).IsRequired();

        builder.HasMany(s => s.Iterations)
            .WithOne()
            .HasForeignKey(i => i.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

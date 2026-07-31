using KrakenDeploy.Server.Core.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserDisplay).HasMaxLength(256).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SubjectType).HasMaxLength(128);
        builder.Property(x => x.SubjectId).HasMaxLength(64);
        builder.Property(x => x.SubjectName).HasMaxLength(256);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(512);

        // Details was the one uncapped string column — Postgres `text` — and it is the one
        // that carries free text from outside the server AND leaves the premises via the
        // subscription poller's webhook / e-mail / AI-inspect transports. The cap is
        // enforced twice on purpose: AuditEntry's setter truncates so no write can throw,
        // and the column stops any future writer that bypasses the entity.
        builder.Property(x => x.Details).HasMaxLength(AuditEntry.MaxDetailsLength);

        // Store JSON snapshots as jsonb so they are queryable in Postgres.
        builder.Property(x => x.BeforeJson).HasColumnType("jsonb");
        builder.Property(x => x.AfterJson).HasColumnType("jsonb");

        // AuditEntry is not ISpaceScoped (no global query filter): background
        // pumps (subscription poller, digest flush) and system-tier
        // diagnostics legitimately read across Spaces. Interactive reads are
        // NOT free-for-all — they must flow through the choke point
        // (AuditExportService.ApplySpaceVisibility): rows are visible in
        // their own Space only, and NULL-SpaceId rows (platform events) only
        // to AdministerSystem holders.
        // No FK constraint — entries must outlive the Space they reference.
        builder.Property(x => x.SpaceId);

        // Retention sweep: delete by OccurredUtc.
        // Filtering by user or event type: covered by the secondary indexes.
        builder.HasIndex(x => x.OccurredUtc);
        builder.HasIndex(x => new { x.UserId, x.OccurredUtc });
        builder.HasIndex(x => new { x.EventType, x.OccurredUtc });
        builder.HasIndex(x => new { x.SpaceId, x.OccurredUtc });
        // "History of this object" (per-entity Events tabs) — without it those
        // queries seq-scan the largest table in the schema.
        builder.HasIndex(x => new { x.SubjectType, x.SubjectId, x.OccurredUtc });
    }
}

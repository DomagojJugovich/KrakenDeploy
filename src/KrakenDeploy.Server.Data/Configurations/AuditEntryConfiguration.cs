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

        // Store JSON snapshots as jsonb so they are queryable in Postgres.
        builder.Property(x => x.BeforeJson).HasColumnType("jsonb");
        builder.Property(x => x.AfterJson).HasColumnType("jsonb");

        // AuditEntry is never space-scoped by the global query filter
        // (we want all entries visible to EventView holders regardless of
        // active Space).  The SpaceId column is just a tag for filtering.
        // No FK constraint — entries must outlive the Space they reference.
        builder.Property(x => x.SpaceId);

        // Retention sweep: delete by OccurredUtc.
        // Filtering by user or event type: covered by the secondary indexes.
        builder.HasIndex(x => x.OccurredUtc);
        builder.HasIndex(x => new { x.UserId, x.OccurredUtc });
        builder.HasIndex(x => new { x.EventType, x.OccurredUtc });
        builder.HasIndex(x => new { x.SpaceId, x.OccurredUtc });
    }
}

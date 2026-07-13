using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class EventSubscriptionConfiguration : IEntityTypeConfiguration<EventSubscription>
{
    public void Configure(EntityTypeBuilder<EventSubscription> builder)
    {
        builder.ToTable("event_subscriptions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.Property(s => s.Transport).HasConversion<int>();
        builder.Property(s => s.TransportConfigJson)
            .HasColumnType("jsonb")
            .HasDefaultValue("{}");

        // jsonb scope lists — same pattern as DeploymentFreeze.
        builder.Property(s => s.EventTypePatterns)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new());

        builder.Property(s => s.ProjectIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new());

        builder.Property(s => s.EnvironmentIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new());

        // Index on (SpaceId, Disabled) so the poller's "find live
        // subscriptions for this Space" query is one B-tree probe per
        // event row instead of a sequential scan.
        builder.HasIndex(s => new { s.SpaceId, s.Disabled });
    }
}

public sealed class SubscriptionDeliveryConfiguration : IEntityTypeConfiguration<SubscriptionDelivery>
{
    public void Configure(EntityTypeBuilder<SubscriptionDelivery> builder)
    {
        builder.ToTable("subscription_deliveries");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Transport).HasConversion<int>();
        builder.Property(d => d.Outcome).HasConversion<int>();
        builder.Property(d => d.Detail).HasMaxLength(4000);
        builder.Property(d => d.ErrorMessage).HasMaxLength(4000);

        // Real FK CASCADE — delivery history dies with its subscription.
        // (No navigation on the plain-Entity delivery row.)
        builder.HasOne<EventSubscription>()
            .WithMany()
            .HasForeignKey(d => d.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index for the per-subscription history grid: pull last 20
        // deliveries by StartedUtc descending.
        builder.HasIndex(d => new { d.SubscriptionId, d.StartedUtc }).IsDescending(false, true);

        // Composite UNIQUE index — the idempotency guard for the poller.
        // If a crash leaves the cursor stale, the next poll might re-
        // process the same audit row; the UNIQUE constraint stops it
        // from emitting a duplicate delivery for the same (subscription,
        // event) pair.
        builder.HasIndex(d => new { d.SubscriptionId, d.EventId }).IsUnique();
    }
}

public sealed class SubscriptionPollerStateConfiguration
    : IEntityTypeConfiguration<SubscriptionPollerState>
{
    public void Configure(EntityTypeBuilder<SubscriptionPollerState> builder)
    {
        builder.ToTable("subscription_poller_state");
        builder.HasKey(s => s.Id);
    }
}

public sealed class EmailDigestOutboxEntryConfiguration
    : IEntityTypeConfiguration<EmailDigestOutboxEntry>
{
    public void Configure(EntityTypeBuilder<EmailDigestOutboxEntry> builder)
    {
        builder.ToTable("email_digest_outbox");
        builder.HasKey(e => e.Id);

        // Real FK CASCADE — queued digest entries die with their subscription.
        builder.HasOne<EventSubscription>()
            .WithMany()
            .HasForeignKey(e => e.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        // UNIQUE (SubscriptionId, EventId) — same idempotency guard as
        // SubscriptionDelivery. A crash-resumed dispatcher must not
        // double-enqueue an event into the outbox.
        builder.HasIndex(e => new { e.SubscriptionId, e.EventId }).IsUnique();

        // The flusher's "what's due to send" query reads by SubscriptionId
        // + AddedUtc, so a composite index keeps that cheap.
        builder.HasIndex(e => new { e.SubscriptionId, e.AddedUtc });
    }
}

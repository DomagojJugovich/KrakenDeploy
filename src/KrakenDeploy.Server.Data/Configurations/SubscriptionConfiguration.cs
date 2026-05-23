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

        // Index for the per-subscription history grid: pull last 20
        // deliveries by StartedUtc descending.
        builder.HasIndex(d => new { d.SubscriptionId, d.StartedUtc }).IsDescending(false, true);

        // Index on EventId so the delivery row can be joined back to the
        // audit_entry that triggered it (M13.B.2/3 UI: "show me what
        // deliveries this event triggered").
        builder.HasIndex(d => d.EventId);
    }
}

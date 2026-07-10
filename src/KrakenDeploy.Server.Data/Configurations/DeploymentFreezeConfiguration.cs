using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Freezes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="DeploymentFreeze"/>. Scope lists (project IDs,
/// environment IDs, tenant-tag canonical names) live in jsonb columns so
/// the freeze stays one self-contained row — joining against a separate
/// freeze_project / freeze_environment table would inflate the query plan
/// for what the DeploymentWorker checks on every dispatch.
/// </summary>
public sealed class DeploymentFreezeConfiguration : IEntityTypeConfiguration<DeploymentFreeze>
{
    public void Configure(EntityTypeBuilder<DeploymentFreeze> builder)
    {
        builder.ToTable("deployment_freezes");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name).IsRequired().HasMaxLength(200);
        builder.Property(f => f.Description).HasMaxLength(2000);

        // Index on Space + window so the dispatcher's "find blocking freeze"
        // query at deployment time stays cheap even with many historical
        // freezes in the table.
        builder.HasIndex(f => new { f.SpaceId, f.StartUtc, f.EndUtc });

        // jsonb columns for the scope lists. Conversion uses System.Text.Json
        // because the lists are plain primitives — no domain types to
        // configure converters for.
        builder.Property(f => f.ProjectIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new());

        builder.Property(f => f.EnvironmentIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new());

        builder.Property(f => f.TagIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new());
    }
}

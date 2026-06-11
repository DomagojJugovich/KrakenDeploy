using KrakenDeploy.Server.Core.Domain.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="PivotView"/>. One row per saved dashboard pivot
/// layout; names are unique per owner so "save" can upsert by (user, name).
/// </summary>
public sealed class PivotViewConfiguration : IEntityTypeConfiguration<PivotView>
{
    public void Configure(EntityTypeBuilder<PivotView> builder)
    {
        builder.ToTable("pivot_views");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Name).IsRequired().HasMaxLength(120);
        builder.Property(v => v.Definition).IsRequired();

        builder.HasIndex(v => new { v.UserId, v.Name }).IsUnique();
    }
}

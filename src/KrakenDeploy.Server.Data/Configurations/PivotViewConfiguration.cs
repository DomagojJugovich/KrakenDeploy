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

        // FK to spaces; the unique (space_id, user_id, name) index below leads
        // with space_id, so no standalone space_id index.
        builder.ConfigureSpaceScope(addSpaceIdIndex: false);

        builder.Property(v => v.Name).IsRequired().HasMaxLength(120);
        builder.Property(v => v.Definition).IsRequired();

        builder.HasIndex(v => new { v.SpaceId, v.UserId, v.Name }).IsUnique();
    }
}

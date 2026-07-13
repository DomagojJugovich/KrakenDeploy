using KrakenDeploy.Server.Core.Domain.Dashboards;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="DashboardLayout"/>. One row per user per dashboard
/// (their saved tile arrangement), so "save" upserts by (space, user, dashboard).
/// </summary>
public sealed class DashboardLayoutConfiguration : IEntityTypeConfiguration<DashboardLayout>
{
    public void Configure(EntityTypeBuilder<DashboardLayout> builder)
    {
        builder.ToTable("dashboard_layouts");
        builder.HasKey(l => l.Id);

        // FK to spaces; the unique (space_id, user_id, dashboard_key) index
        // below leads with space_id, so no standalone space_id index.
        builder.ConfigureSpaceScope(addSpaceIdIndex: false);

        builder.Property(l => l.DashboardKey).IsRequired().HasMaxLength(64);
        builder.Property(l => l.Definition).IsRequired();

        builder.HasIndex(l => new { l.SpaceId, l.UserId, l.DashboardKey }).IsUnique();
    }
}

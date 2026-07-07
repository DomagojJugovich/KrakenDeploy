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

        builder.Property(l => l.DashboardKey).IsRequired().HasMaxLength(64);
        builder.Property(l => l.Definition).IsRequired();

        builder.HasIndex(l => new { l.SpaceId, l.UserId, l.DashboardKey }).IsUnique();
    }
}

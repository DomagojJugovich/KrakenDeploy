using KrakenDeploy.Server.Core.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="ProjectDashboardView"/>. One row per user (their
/// saved default Projects-dashboard filter), so "save" upserts by user id.
/// </summary>
public sealed class ProjectDashboardViewConfiguration : IEntityTypeConfiguration<ProjectDashboardView>
{
    public void Configure(EntityTypeBuilder<ProjectDashboardView> builder)
    {
        builder.ToTable("project_dashboard_views");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Definition).IsRequired();

        builder.HasIndex(v => new { v.SpaceId, v.UserId }).IsUnique();
    }
}

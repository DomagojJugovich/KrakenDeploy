using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Data.Identity;
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

        // FK to spaces; the unique (space_id, user_id) index below leads with
        // space_id, so no standalone space_id index.
        builder.ConfigureSpaceScope(addSpaceIdIndex: false);

        builder.Property(v => v.Definition).IsRequired();

        builder.HasIndex(v => new { v.SpaceId, v.UserId }).IsUnique();

        // Private per-user view dies with its owner (CASCADE).
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

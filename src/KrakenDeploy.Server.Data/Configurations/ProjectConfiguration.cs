using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.SpaceId, x.Slug }).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(2000);

        // RESTRICT — deleting a lifecycle that gates a project must fail
        // loudly, not silently null the pointer and un-gate deploys. Composite
        // Space FK: the lifecycle must live in the project's Space (Project keeps
        // its own direct spaces FK as an aggregate root; this FK is composite
        // only to prevent a cross-Space reference).
        builder.HasOne(x => x.Lifecycle)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.LifecycleId })
            .HasPrincipalKey(l => new { l.SpaceId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // ProjectGroup FK — required (M10 transition complete). RESTRICT: a
        // group can't be deleted while it still holds projects. Composite Space
        // FK: the group must live in the project's Space.
        builder.HasOne(x => x.ProjectGroup)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.ProjectGroupId })
            .HasPrincipalKey(g => new { g.SpaceId, g.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(x => x.ProjectGroupId);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

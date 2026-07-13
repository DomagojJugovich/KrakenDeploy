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
        // loudly, not silently null the pointer and un-gate deploys.
        builder.HasOne(x => x.Lifecycle)
            .WithMany()
            .HasForeignKey(x => x.LifecycleId)
            .OnDelete(DeleteBehavior.Restrict);

        // ProjectGroup FK — required (M10 transition complete). RESTRICT: a
        // group can't be deleted while it still holds projects.
        builder.HasOne(x => x.ProjectGroup)
            .WithMany()
            .HasForeignKey(x => x.ProjectGroupId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasIndex(x => x.ProjectGroupId);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

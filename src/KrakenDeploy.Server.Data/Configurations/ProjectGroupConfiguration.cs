using KrakenDeploy.Server.Core.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class ProjectGroupConfiguration : IEntityTypeConfiguration<ProjectGroup>
{
    public void Configure(EntityTypeBuilder<ProjectGroup> builder)
    {
        builder.ToTable("project_groups");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.SpaceId, x.Slug }).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.SortOrder).IsRequired();
        builder.Property(x => x.IsDefault).IsRequired();

        // At most one Default Project Group per Space.
        builder.HasIndex(x => new { x.SpaceId, x.IsDefault })
            .HasFilter("\"is_default\" = true")
            .IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

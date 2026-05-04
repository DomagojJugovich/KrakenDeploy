using KrakenDeploy.Server.Core.Domain.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class DeploymentEnvironmentConfiguration : IEntityTypeConfiguration<DeploymentEnvironment>
{
    public void Configure(EntityTypeBuilder<DeploymentEnvironment> builder)
    {
        builder.ToTable("environments");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.SpaceId, x.Slug }).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        builder.Property(x => x.SortOrder).IsRequired();
        builder.HasIndex(x => x.SortOrder);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

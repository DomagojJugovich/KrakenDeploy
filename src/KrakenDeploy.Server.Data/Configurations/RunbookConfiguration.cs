using KrakenDeploy.Server.Core.Domain.Runbooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class RunbookConfiguration : IEntityTypeConfiguration<Runbook>
{
    public void Configure(EntityTypeBuilder<Runbook> builder)
    {
        builder.ToTable("runbooks");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);

        // Composite Space FK: a runbook can only belong to a project in its Space.
        builder.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.ProjectId })
            .HasPrincipalKey(p => new { p.SpaceId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

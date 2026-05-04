using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class VariableSetConfiguration : IEntityTypeConfiguration<VariableSet>
{
    public void Configure(EntityTypeBuilder<VariableSet> builder)
    {
        builder.ToTable("variable_sets");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.HasOne(x => x.Project)
            .WithOne(p => p.VariableSet)
            .HasForeignKey<VariableSet>(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ProjectId).IsUnique();
    }
}

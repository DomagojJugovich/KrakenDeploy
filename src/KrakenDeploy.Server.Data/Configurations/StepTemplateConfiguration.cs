using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class StepTemplateConfiguration : IEntityTypeConfiguration<StepTemplate>
{
    public void Configure(EntityTypeBuilder<StepTemplate> builder)
    {
        builder.ToTable("step_templates");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(4096);
        builder.Property(x => x.ActionType).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.CommunityTemplateId).HasMaxLength(256);
        builder.Property(x => x.ImportedFrom).HasMaxLength(1024);

        builder.Property(x => x.Properties)
            .HasJsonbColumn<Dictionary<string, string>>();

        builder.Property(x => x.Parameters)
            .HasJsonbColumn<List<StepTemplateParameter>>();

        builder.HasIndex(x => x.ActionType);
        builder.HasIndex(x => x.Name);
        builder.HasIndex(x => x.CommunityTemplateId);
    }
}

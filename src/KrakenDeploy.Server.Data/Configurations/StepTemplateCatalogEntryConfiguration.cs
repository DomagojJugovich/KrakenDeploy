using KrakenDeploy.Server.Core.Domain.StepTemplates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public sealed class StepTemplateCatalogEntryConfiguration
    : IEntityTypeConfiguration<StepTemplateCatalogEntry>
{
    public void Configure(EntityTypeBuilder<StepTemplateCatalogEntry> builder)
    {
        builder.ToTable("step_template_catalog");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CommunityTemplateId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.FeedKey).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.FeedKey);
        builder.Property(x => x.PathInRepo).HasMaxLength(512).IsRequired();
        builder.Property(x => x.FileSha).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DownloadUrl).HasMaxLength(1024).IsRequired();

        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ActionType).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(4096);
        builder.Property(x => x.Category).HasMaxLength(128);
        builder.Property(x => x.Author).HasMaxLength(256);
        builder.Property(x => x.Website).HasMaxLength(1024);
        builder.Property(x => x.LogoUrl).HasMaxLength(1024);

        builder.HasIndex(x => x.CommunityTemplateId).IsUnique();
        builder.HasIndex(x => x.Category);
        builder.HasIndex(x => x.LastSyncedUtc);
    }
}

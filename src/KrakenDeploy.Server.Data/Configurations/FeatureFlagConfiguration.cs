using KrakenDeploy.Server.Core.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="FeatureFlag"/>. Key is the logical identifier
/// (unique); Id stays Guid PK for consistency with the rest of the schema
/// and so the audit interceptor picks the row up via the usual
/// AuditableEntity convention.
/// </summary>
public sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.ToTable("feature_flags");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Key).IsRequired().HasMaxLength(128);
        builder.HasIndex(f => f.Key).IsUnique();
    }
}

using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class VariableConfiguration : IEntityTypeConfiguration<Variable>
{
    public void Configure(EntityTypeBuilder<Variable> builder)
    {
        builder.ToTable("variables");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        // Value: plain string, AES-GCM ciphertext (base64), or JSON array string.
        builder.Property(x => x.Value).IsRequired();

        // Store the enum as a readable string rather than an integer.
        builder.Property(x => x.Type)
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(x => x.IsPrompted).HasDefaultValue(false);
        builder.Property(x => x.PromptLabel).HasMaxLength(200);
        builder.Property(x => x.PromptDescription).HasMaxLength(2000);
        builder.Property(x => x.PromptRequired).HasDefaultValue(false);
        builder.Property(x => x.PromptControl)
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasDefaultValue(PromptControlType.Text);
        builder.Property(x => x.PromptOptions).HasJsonbColumn<List<string>>();

        // Scope stored as jsonb for flexible querying / future indexing.
        builder.Property(x => x.Scope)
            .HasJsonbColumn<VariableScope>();

        // Composite Space FK: a variable can only belong to a set in its own Space.
        builder.HasOne(x => x.Set)
            .WithMany(s => s.Variables)
            .HasForeignKey(x => new { x.SpaceId, x.SetId })
            .HasPrincipalKey(s => new { s.SpaceId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Most queries filter by set and optionally name.
        builder.HasIndex(x => new { x.SetId, x.Name });
    }
}

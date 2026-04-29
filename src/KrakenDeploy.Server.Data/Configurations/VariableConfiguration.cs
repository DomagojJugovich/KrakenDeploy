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

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        // Value: plain string, AES-GCM ciphertext (base64), or JSON array string.
        builder.Property(x => x.Value).IsRequired();

        // Store the enum as a readable string rather than an integer.
        builder.Property(x => x.Type)
            .HasMaxLength(32)
            .HasConversion<string>()
            .IsRequired();

        // Scope stored as jsonb for flexible querying / future indexing.
        builder.Property(x => x.Scope)
            .HasJsonbColumn<VariableScope>();

        builder.HasOne(x => x.Set)
            .WithMany(s => s.Variables)
            .HasForeignKey(x => x.SetId)
            .OnDelete(DeleteBehavior.Cascade);

        // Most queries filter by set and optionally name.
        builder.HasIndex(x => new { x.SetId, x.Name });
    }
}

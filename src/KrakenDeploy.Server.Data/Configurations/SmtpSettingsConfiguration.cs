using KrakenDeploy.Server.Core.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="SmtpSettings"/>. Single-row table — uniqueness
/// is enforced at the service layer (FindAsync(SingletonId)), not via DB
/// constraints, so a stray test row doesn't break startup.
/// </summary>
public sealed class SmtpSettingsConfiguration : IEntityTypeConfiguration<SmtpSettings>
{
    public void Configure(EntityTypeBuilder<SmtpSettings> builder)
    {
        builder.ToTable("smtp_settings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Host).IsRequired().HasMaxLength(255);
        builder.Property(s => s.Username).HasMaxLength(255);
        builder.Property(s => s.PasswordEncrypted).HasMaxLength(2048);
        builder.Property(s => s.FromAddress).IsRequired().HasMaxLength(320); // RFC 5321 max
        builder.Property(s => s.FromDisplayName).HasMaxLength(255);

        // Store enum as integer (default for EF; explicit so the migration
        // doesn't drift if we add HasConversion<string>() elsewhere).
        builder.Property(s => s.TlsMode).HasConversion<int>();
    }
}

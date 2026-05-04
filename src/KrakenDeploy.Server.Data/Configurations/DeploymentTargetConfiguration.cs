using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class DeploymentTargetConfiguration : IEntityTypeConfiguration<DeploymentTarget>
{
    public void Configure(EntityTypeBuilder<DeploymentTarget> builder)
    {
        builder.ToTable("deployment_targets");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);

        builder.Property(x => x.LastSeenUtc);

        builder.Property(x => x.MachineName).HasMaxLength(255);
        builder.Property(x => x.OperatingSystem).HasMaxLength(255);
        builder.Property(x => x.AgentVersion).HasMaxLength(64);

        // Roles → text[]; Npgsql maps List<string> to text[] natively.
        builder.Property(x => x.Roles)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(x => x.TransportMode).IsRequired().HasConversion<int>();

        builder.Property(x => x.RegistrationKeyHash).HasMaxLength(128);
        builder.Property(x => x.RegistrationTokenExpiresUtc);

        // Offline-drop configuration — nullable JSONB, only populated when
        // TransportMode == OfflineDrop. Suppressing CS8620: the converter handles
        // null values correctly via EF Core's built-in null propagation.
#pragma warning disable CS8620
        builder.Property(x => x.OfflineDropConfig)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<OfflineDropConfig>());
#pragma warning restore CS8620

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

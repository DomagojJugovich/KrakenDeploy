using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
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

        // Risk classification (M11.E.11). Default Production (fail-safe) so the
        // DB-default backfills existing rows to highest-risk until classified.
        // HasSentinel(Production) is REQUIRED alongside HasDefaultValue: without
        // it EF treats the CLR-default enum value Development(0) as "not set" and
        // lets the store default overwrite it, so Development would never persist.
        // With the sentinel set to Production, EF only omits the value when it
        // equals Production (→ store default applies), and always writes
        // Development/Staging.
        builder.Property(x => x.RiskLevel)
            .IsRequired()
            .HasConversion<int>()
            .HasDefaultValue(TargetRiskLevel.Production)
            .HasSentinel(TargetRiskLevel.Production);
        builder.HasIndex(x => x.RiskLevel);

        builder.Property(x => x.AutoUpdateEnabled).IsRequired().HasDefaultValue(true);

        // Soft-delete / decommission flag. Store-default false backfills existing
        // rows; false is the CLR default too, so no HasSentinel dance is needed.
        builder.Property(x => x.IsRetired).IsRequired().HasDefaultValue(false);

        builder.Property(x => x.RegistrationKeyHash).HasMaxLength(128);
        builder.Property(x => x.RegistrationTokenExpiresUtc);

        // A8/T1-12: agent-token version (the JWT `atv` claim). Store-default 0 so
        // it backfills existing rows; 0 is the natural CLR default too, so no
        // HasSentinel dance is needed (unlike RiskLevel above).
        builder.Property(x => x.AgentTokenVersion).IsRequired().HasDefaultValue(0);

        // Offline-drop configuration — nullable JSONB, only populated when
        // TransportMode == OfflineDrop. Suppressing CS8620: the converter handles
        // null values correctly via EF Core's built-in null propagation.
#pragma warning disable CS8620
        builder.Property(x => x.OfflineDropConfig)
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<OfflineDropConfig>());
#pragma warning restore CS8620

        // Direct tenant association (Octopus "Associated Tenants") — the
        // primary tenant↔target link. Distinct from target_tenant_tags,
        // which carries auxiliary tag metadata. Explicit Space-scoped join:
        // composite FKs on BOTH sides pin target and tenant to the same Space.
        builder.HasMany(x => x.Tenants)
            .WithMany()
            .UsingEntity<TargetTenant>(
                r => r.HasOne<Tenant>()
                    .WithMany()
                    .HasForeignKey(tt => new { tt.SpaceId, tt.TenantId })
                    .HasPrincipalKey(t => new { t.SpaceId, t.Id })
                    .OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<DeploymentTarget>()
                    .WithMany()
                    .HasForeignKey(tt => new { tt.SpaceId, tt.TargetId })
                    .HasPrincipalKey(d => new { d.SpaceId, d.Id })
                    .OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("target_tenants");
                    j.HasKey(tt => new { tt.TargetId, tt.TenantId });
                    j.Property(tt => tt.SpaceId).IsRequired();
                    j.HasIndex(tt => tt.TenantId);
                });

        // Environments this target serves. Explicit Space-scoped join, same shape.
        builder.HasMany(x => x.Environments)
            .WithMany()
            .UsingEntity<TargetEnvironment>(
                r => r.HasOne<DeploymentEnvironment>()
                    .WithMany()
                    .HasForeignKey(te => new { te.SpaceId, te.EnvironmentId })
                    .HasPrincipalKey(e => new { e.SpaceId, e.Id })
                    .OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<DeploymentTarget>()
                    .WithMany()
                    .HasForeignKey(te => new { te.SpaceId, te.TargetId })
                    .HasPrincipalKey(d => new { d.SpaceId, d.Id })
                    .OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("target_environments");
                    j.HasKey(te => new { te.TargetId, te.EnvironmentId });
                    j.Property(te => te.SpaceId).IsRequired();
                    j.HasIndex(te => te.EnvironmentId);
                });

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

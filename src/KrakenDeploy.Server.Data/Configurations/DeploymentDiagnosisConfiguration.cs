using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF config for <see cref="DeploymentDiagnosis"/> (M11.C). One row per
/// deployment enforced by a unique index on
/// <see cref="DeploymentDiagnosis.DeploymentId"/> — re-diagnosis upserts.
/// </summary>
public sealed class DeploymentDiagnosisConfiguration
    : IEntityTypeConfiguration<DeploymentDiagnosis>
{
    public void Configure(EntityTypeBuilder<DeploymentDiagnosis> builder)
    {
        builder.ToTable("deployment_diagnoses");
        builder.HasKey(x => x.Id);

        // Child of ServerTask — space_id is transitively guaranteed by the
        // composite FK below; the FK's covering index (space_id, deployment_id)
        // serves the Space query filter, so no direct spaces FK / standalone index.
        builder.ConfigureSpaceScopeAsChild();

        builder.HasIndex(x => x.DeploymentId).IsUnique();

        // The diagnosis is for one server_tasks row (a Kind=Deployment task,
        // post fix-3). CASCADE: pruning the task drops its diagnosis. The
        // unique index above enforces the one-to-one shape. Composite Space FK
        // so a diagnosis can only reference a task in its own Space.
        builder.HasOne<ServerTask>()
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.DeploymentId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ProbableCause).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.SuggestedFix).HasMaxLength(2000).IsRequired();
        // jsonb display blob — relevant log lines. Bounded by the assembler
        // (a handful of lines) but the column is unconstrained text.
        builder.Property(x => x.RelevantLogLinesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.ModelUsed).HasMaxLength(256);
    }
}

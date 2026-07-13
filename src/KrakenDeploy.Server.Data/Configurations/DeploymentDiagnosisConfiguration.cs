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

        // ISpaceScoped but historically missing its Space FK/index — add both
        // (this table has no space_id index yet, so create a standalone one).
        builder.ConfigureSpaceScope();

        builder.HasIndex(x => x.DeploymentId).IsUnique();

        // The diagnosis is for one server_tasks row (a Kind=Deployment task,
        // post fix-3). CASCADE: pruning the task drops its diagnosis. The
        // unique index above enforces the one-to-one shape.
        builder.HasOne<ServerTask>()
            .WithMany()
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ProbableCause).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.SuggestedFix).HasMaxLength(2000).IsRequired();
        // jsonb display blob — relevant log lines. Bounded by the assembler
        // (a handful of lines) but the column is unconstrained text.
        builder.Property(x => x.RelevantLogLinesJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.ModelUsed).HasMaxLength(256);
    }
}

using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for <see cref="DeploymentStepOutcome"/>. The natural key is
/// (DeploymentId, StepIndex); we keep the generated <c>Id</c> as the
/// surrogate PK to stay consistent with <see cref="DeploymentOutputVariable"/>
/// and the rest of the data layer, plus a unique index on (DeploymentId,
/// StepIndex) so the orchestrator + agent can upsert by natural key.
/// </summary>
public sealed class DeploymentStepOutcomeConfiguration
    : IEntityTypeConfiguration<DeploymentStepOutcome>
{
    public void Configure(EntityTypeBuilder<DeploymentStepOutcome> builder)
    {
        builder.ToTable("deployment_step_outcomes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StepIndex).IsRequired();
        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Outcome).HasConversion<int>().IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.ErrorMessage);
        builder.Property(x => x.StartedUtc);
        builder.Property(x => x.CompletedUtc).IsRequired();
        builder.Property(x => x.IsServerSide).IsRequired();
        builder.Property(x => x.Required).IsRequired();

        builder.HasOne(x => x.Deployment)
            .WithMany()
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Primary read pattern: "give me every step's outcome for this
        // deployment, in step order" — the Steps tab on the deployment
        // detail page. The composite index serves both that scan and
        // the upsert-by-natural-key path.
        builder.HasIndex(x => new { x.DeploymentId, x.StepIndex }).IsUnique();
    }
}

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

        builder.ConfigureSpaceScope();

        builder.Property(x => x.StepIndex).IsRequired();
        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Outcome).HasConversion<int>().IsRequired();
        builder.Property(x => x.AttemptCount).IsRequired();
        builder.Property(x => x.ErrorMessage);
        builder.Property(x => x.StartedUtc);
        builder.Property(x => x.CompletedUtc).IsRequired();
        builder.Property(x => x.IsServerSide).IsRequired();
        builder.Property(x => x.Required).IsRequired();
        builder.Property(x => x.TargetId);

        // Real FK (was a bare column that could dangle after target deletes).
        // Restrict matches the assignments join + runbook_runs: execution
        // history pins its targets; deletion goes through the archived-flag
        // escape hatch (fix 4), never by orphaning history rows. No
        // navigation property — outcomes are read per deployment, and the
        // detail page resolves names through the deployment's target set.
        builder.HasOne<Core.Domain.Targets.DeploymentTarget>()
            .WithMany()
            .HasForeignKey(x => x.TargetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Deployment)
            .WithMany()
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Primary read pattern: "give me every step's outcome for this
        // deployment, in step order" — the Steps tab on the deployment
        // detail page. The composite unique index serves both that scan
        // and the upsert-by-natural-key path.
        //
        // M-RollingDeployments groundwork: index widens to include
        // TargetId so multi-target dispatch can write one outcome row
        // per (deployment, step, target). NULL TargetId distinguishes
        // server-side steps that aren't bound to a specific target.
        // Postgres treats NULL as distinct in unique indexes by default,
        // so two rows with (Dep=X, Step=Y, Target=NULL) would collide
        // only if both are server-side server-once steps — which is
        // the intended semantic.
        builder.HasIndex(x => new { x.DeploymentId, x.StepIndex, x.TargetId }).IsUnique();
    }
}

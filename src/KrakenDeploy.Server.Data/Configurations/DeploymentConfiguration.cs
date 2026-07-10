using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployments");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);

        // Rolling-deployment failure handling. Stored as int (0 = BestEffort).
        builder.Property(x => x.FailureMode).IsRequired().HasConversion<int>();

        builder.Property(x => x.StartedUtc);
        builder.Property(x => x.CompletedUtc);
        builder.Property(x => x.ScheduledFor);
        // Partial index — only rows waiting to be dispatched need to be scanned.
        builder.HasIndex(x => x.ScheduledFor)
            .HasFilter("scheduled_for IS NOT NULL AND status = 0");
        // Status 0 = Queued (enum int stored as int column).

        builder.HasOne(x => x.Release)
            .WithMany()
            .HasForeignKey(x => x.ReleaseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => x.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Targets: exclusively via the deployment_target_assignments join
        // (DeploymentTargetAssignmentConfiguration, delete = Restrict). The
        // transitional deployments.target_id column was dropped in the
        // 2026-07 schema hardening — one authority, one delete policy.

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.SetNull);

        // Serves the release×environment matrix cells ("latest deployment of
        // release R into environment E") and lifecycle-gate condition checks.
        builder.HasIndex(x => new { x.ReleaseId, x.EnvironmentId });

        // Parent-deployment link — set when an Octopus.DeployRelease step in
        // another deployment triggered this one. SetNull on delete so deleting
        // a parent doesn't cascade away its child's history.
        builder.HasOne(x => x.ParentDeployment)
            .WithMany()
            .HasForeignKey(x => x.ParentDeploymentId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.ParentDeploymentId)
            .HasFilter("parent_deployment_id IS NOT NULL");

        // Relative path to the drop-bundle zip for offline-drop deployments.
        builder.Property(x => x.DropBundlePath).HasMaxLength(500);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

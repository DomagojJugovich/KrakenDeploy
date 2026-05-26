using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// EF mapping for the M-RollingDeployments groundwork join row
/// <see cref="DeploymentTargetAssignment"/>. Composite primary key on
/// (DeploymentId, TargetId) — same target can't be assigned twice to
/// the same deployment. Cascading FK from the deployment side so
/// deleting a deployment removes its assignments atomically; the
/// target-side FK does NOT cascade (deleting a machine shouldn't
/// silently rip out historical deployment assignments — operators
/// must clean up the assignment rows or accept that the FK is
/// dangling, which RESTRICT enforces).
/// </summary>
public sealed class DeploymentTargetAssignmentConfiguration
    : IEntityTypeConfiguration<DeploymentTargetAssignment>
{
    public void Configure(EntityTypeBuilder<DeploymentTargetAssignment> builder)
    {
        // Table is deliberately NOT named "deployment_targets" — that
        // name is already taken by the DeploymentTarget (machine)
        // entity in the Targets aggregate. "deployment_target_assignments"
        // is more accurate anyway: the row is a "deployment X is
        // assigned to target Y" link.
        builder.ToTable("deployment_target_assignments");

        builder.HasKey(x => new { x.DeploymentId, x.TargetId });

        builder.Property(x => x.AddedUtc).IsRequired();

        builder.HasOne(x => x.Deployment)
            .WithMany(d => d.Targets)
            .HasForeignKey(x => x.DeploymentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Target)
            .WithMany()
            .HasForeignKey(x => x.TargetId)
            .OnDelete(DeleteBehavior.Restrict);

        // Reverse-lookup index: "which deployments hit target X?" The
        // composite PK already covers (DeploymentId, TargetId); we add
        // a TargetId-only index so the inverse query is cheap too.
        builder.HasIndex(x => x.TargetId);
    }
}

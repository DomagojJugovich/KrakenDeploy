using KrakenDeploy.Server.Core.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// TPH-derived mapping for <see cref="Deployment"/> — adds the deployment-only
/// <c>release_id</c> column/FK to the shared <c>server_tasks</c> table. No
/// <c>ToTable</c>/<c>HasKey</c> (inherited from <see cref="ServerTaskConfiguration"/>).
/// </summary>
public class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        // Execution history is delete-proof: a release with deployments cannot be
        // deleted out from under them (decision 7).
        builder.HasOne(x => x.Release)
            .WithMany()
            .HasForeignKey(x => x.ReleaseId)
            .OnDelete(DeleteBehavior.Restrict);

        // Serves the release×environment matrix cells ("latest deployment of
        // release R into environment E") and lifecycle-gate condition checks.
        builder.HasIndex(x => new { x.ReleaseId, x.EnvironmentId });
    }
}

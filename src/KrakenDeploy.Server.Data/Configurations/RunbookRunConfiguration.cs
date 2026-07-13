using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// TPH-derived mapping for <see cref="RunbookRun"/> — adds the runbook-only
/// <c>runbook_id</c> column/FK and the frozen <c>process_snapshot</c> jsonb to the
/// shared <c>server_tasks</c> table. No <c>ToTable</c>/<c>HasKey</c> (inherited from
/// <see cref="ServerTaskConfiguration"/>).
/// </summary>
public class RunbookRunConfiguration : IEntityTypeConfiguration<RunbookRun>
{
    public void Configure(EntityTypeBuilder<RunbookRun> builder)
    {
        builder.Property(x => x.ProcessSnapshot)
            .HasJsonbColumn<List<StepSnapshot>>();

        // RESTRICT, not Cascade (decision 7): runbook run history is execution
        // history — deleting a runbook must not mass-cascade its runs away. This
        // fixes the pre-unification asymmetry where runbook_runs cascaded.
        // Composite Space FK so a run can only reference a runbook in its own Space
        // (runbook_id is physically nullable in TPH → FK skipped for Deployment
        // rows under MATCH SIMPLE).
        builder.HasOne(x => x.Runbook)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.RunbookId })
            .HasPrincipalKey(r => new { r.SpaceId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.RunbookId);
    }
}

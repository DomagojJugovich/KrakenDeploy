using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class RunbookRunConfiguration : IEntityTypeConfiguration<RunbookRun>
{
    public void Configure(EntityTypeBuilder<RunbookRun> builder)
    {
        builder.ToTable("runbook_runs");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);

        builder.Property(x => x.ProcessSnapshot)
            .HasJsonbColumn<List<StepSnapshot>>();

        builder.HasOne(x => x.Runbook)
            .WithMany()
            .HasForeignKey(x => x.RunbookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => x.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not SetNull: runbook runs are execution history — a
        // target that has run history must not be deletable out from under
        // it (nulling the FK silently orphans the run's attribution). The
        // soft-disable escape hatch (target archived flag) is fix 4.
        builder.HasOne(x => x.Target)
            .WithMany()
            .HasForeignKey(x => x.TargetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(x => x.RunbookId);

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

public class RunbookRunLogEntryConfiguration : IEntityTypeConfiguration<RunbookRunLogEntry>
{
    public void Configure(EntityTypeBuilder<RunbookRunLogEntry> builder)
    {
        builder.ToTable("runbook_run_log_entries");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Level).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Message).IsRequired();

        builder.HasOne(x => x.RunbookRun)
            .WithMany(r => r.LogEntries)
            .HasForeignKey(x => x.RunbookRunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.RunbookRunId, x.Sequence }).IsUnique();
    }
}

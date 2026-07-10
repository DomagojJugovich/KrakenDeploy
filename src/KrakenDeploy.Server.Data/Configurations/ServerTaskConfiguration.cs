using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// TPH root mapping for the unified <see cref="ServerTask"/> spine
/// (<c>server_tasks</c>). Derived <see cref="Deployment"/> / <see cref="RunbookRun"/>
/// specifics live in their own configs; the discriminator + all shared columns are
/// here. A CHECK constraint enforces "exactly one of release_id/runbook_id, matching
/// kind" so the polymorphic ownership can't drift.
/// </summary>
public sealed class ServerTaskConfiguration : IEntityTypeConfiguration<ServerTask>
{
    public void Configure(EntityTypeBuilder<ServerTask> builder)
    {
        builder.ToTable("server_tasks", t => t.HasCheckConstraint(
            "ck_server_tasks_kind_owner",
            // kind 0 = Deployment (release_id set), kind 1 = RunbookRun (runbook_id set).
            "(kind = 0 AND release_id IS NOT NULL AND runbook_id IS NULL) OR " +
            "(kind = 1 AND runbook_id IS NOT NULL AND release_id IS NULL)"));
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        // TPH discriminator (stored as int).
        builder.HasDiscriminator(x => x.Kind)
            .HasValue<Deployment>(ServerTaskKind.Deployment)
            .HasValue<RunbookRun>(ServerTaskKind.RunbookRun);

        builder.Property(x => x.Status).IsRequired().HasConversion<int>();
        builder.HasIndex(x => x.Status);

        builder.Property(x => x.FailureMode).IsRequired().HasConversion<int>();

        builder.Property(x => x.StartedUtc);
        builder.Property(x => x.CompletedUtc);
        builder.Property(x => x.ScheduledFor);
        // Partial index — only rows waiting to be dispatched need scanning by the
        // scheduled-dispatch job (status 0 = Queued).
        builder.HasIndex(x => x.ScheduledFor)
            .HasFilter("scheduled_for IS NOT NULL AND status = 0");

        builder.Property(x => x.NextLogSequence).IsRequired();

        // ── Denormalized ownership (decision 5) ──────────────────────────────
        builder.Property(x => x.ProjectId).IsRequired();
        // Dashboards / pivot / project matrix filter by project (and tenant) —
        // drops the task -> release -> project join.
        builder.HasIndex(x => x.ProjectId);
        builder.Property(x => x.ChannelId);

        // Inert future prompted-variable values.
        builder.Property(x => x.FormValues).HasColumnType("jsonb");

        builder.Property(x => x.DropBundlePath).HasMaxLength(500);

        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => x.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.SetNull);

        // Parent-task link — set when an Octopus.DeployRelease step triggered this
        // task. SetNull on delete so deleting a parent doesn't cascade away the
        // child's history.
        builder.HasOne(x => x.ParentTask)
            .WithMany()
            .HasForeignKey(x => x.ParentTaskId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.ParentTaskId)
            .HasFilter("parent_task_id IS NOT NULL");

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

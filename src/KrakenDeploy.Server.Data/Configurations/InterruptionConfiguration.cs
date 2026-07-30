using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for <see cref="Interruption"/> (<c>interruptions</c>) — WP3's
/// manual-intervention gate. Surrogate <c>Id</c> PK plus a unique natural key
/// (TaskId, StepIndex): a step gates at most once per task, so the orchestrator's
/// pause write is an upsert-safe insert even if a duplicate wake-up races it.
/// Mirrors <see cref="TaskStepOutcomeConfiguration"/> for the Space-scoping
/// convention.
/// </summary>
public sealed class InterruptionConfiguration : IEntityTypeConfiguration<Interruption>
{
    public void Configure(EntityTypeBuilder<Interruption> builder)
    {
        builder.ToTable("interruptions");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScopeAsChild();

        builder.Property(x => x.StepIndex).IsRequired();
        builder.Property(x => x.StepName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Instructions);
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.ExpiresUtc);
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.ActedUtc);
        builder.Property(x => x.ActedByDisplay).HasMaxLength(TaskInitiator.MaxDisplayLength);
        builder.Property(x => x.Notes).HasMaxLength(4000);

        // Snapshot of the responsible teams — a bare Guid array, NOT a join table
        // with an FK to `teams`. A team must stay deletable without rewriting who was
        // asked, and a real FK would force us either to block team deletion or to
        // cascade the answer away mid-window. Postgres uuid[] maps natively, so no
        // value converter is needed. Never queried server-side (membership is an
        // in-memory set test), so it needs no index.
        builder.Property(x => x.ResponsibleTeamIds)
            .HasColumnType("uuid[]")
            .IsRequired();

        // WP3-b — names alongside the ids, because the name is usually NOT recoverable
        // when it is needed: break-glass exists for the case where the named team was
        // deleted while the gate waited, and resolving names at decision time would
        // render the change-control record as bare GUIDs in exactly that case.
        builder.Property(x => x.ResponsibleTeamNames)
            .HasColumnType("text[]")
            .IsRequired();

        // Real FK to users with SET NULL — an interruption is user-ANSWERED, not
        // user-owned; the change-control trail must outlive the responder's
        // deletion (the denormalized acted_by_display keeps it readable). Same
        // shape as server_tasks.created_by_user_id.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.ActedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Composite Space FK: an interruption can only belong to a task in its
        // Space (house rule 4). CASCADE with the task — deleting a task's history
        // takes its gates with it.
        builder.HasOne(x => x.Task)
            .WithMany(t => t.Interruptions)
            .HasForeignKey(x => new { x.SpaceId, x.TaskId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // One gate per (task, step) — also the orchestrator's insert guard.
        builder.HasIndex(x => new { x.TaskId, x.StepIndex }).IsUnique();

        // The timeout sweeper's hot path: PENDING gates past their expiry. Partial
        // index on status 0 (Pending) AND a non-null expiry keeps it to the handful
        // of gates actually awaiting an answer, however long the history grows.
        builder.HasIndex(x => x.ExpiresUtc)
            .HasDatabaseName("ix_interruptions_pending_expiry")
            .HasFilter("status = 0 AND expires_utc IS NOT NULL");
    }
}

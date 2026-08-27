using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Data.Identity;
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

        // ── Dispatch lease (B1) ──────────────────────────────────────────────
        // ClaimedBy is forensic only; LeaseUntil drives the orphan reconciler.
        // No extra index: the reconciler filters on Status (indexed above) and
        // the Running set is small.
        builder.Property(x => x.ClaimedBy).HasMaxLength(128);
        builder.Property(x => x.LeaseUntil);

        // ── Optimistic concurrency (B5) ──────────────────────────────────────
        // Postgres's xmin system column as the row-version token: every tracked
        // UPDATE of a ServerTask carries WHERE xmin = <original>, so two status
        // writers can't silently last-writer-win each other (cancel vs late
        // completion, finalize vs reconciler). No DDL — xmin exists on every
        // row already. IMPORTANT: xmin changes on ANY update of the row. The
        // B1 lease renewal bypasses the change tracker, so a long-lived tracked
        // entity's token goes stale within seconds of dispatch. Status writers
        // therefore MUST go through ServerTaskStatusWriter (reload → guard →
        // write) instead of saving a stale tracked instance directly. (E-D
        // moved the log-sequence counter off this row into task_log_counters so
        // log appends no longer churn xmin.)
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();

        // ── Denormalized ownership (decision 5) ──────────────────────────────
        builder.Property(x => x.ProjectId).IsRequired();
        // Dashboards / pivot / project matrix filter by project (and tenant) —
        // drops the task -> release -> project join.
        builder.HasIndex(x => x.ProjectId);
        builder.Property(x => x.ChannelId);

        // F1 — (project, environment, tenant) serialization. The claim's peer
        // check (ServerTaskLease.InFlightDeploymentPeerPredicate), the worker's
        // pre-gate skip and the UI queue-reason read all probe for ANOTHER
        // IN-FLIGHT deployment of the same key, and the claim path runs on every
        // dispatch. Partial index on the IN-FLIGHT-DEPLOYMENT set only (status IN
        // (1 Running, 5 PendingOfflineResult, 7 Paused) AND kind 0 = Deployment —
        // the same literal-enum filter idiom as the scheduled_for index above, and
        // it MUST match DeploymentStatusExtensions.InFlightAfterClaim or Postgres
        // won't use it for the IN (...) predicate) keeps it tiny: that set is
        // bounded by the node concurrency cap × node count plus any parked offline
        // drops and paused approval gates, so the index stays a handful of rows
        // however long the history grows. WP3 added 7 (Paused) — a paused task
        // still holds its (project, environment, tenant) key.
        builder.HasIndex(x => new { x.ProjectId, x.EnvironmentId, x.TenantId })
            .HasDatabaseName("ix_server_tasks_running_deployment_peer")
            .HasFilter("status IN (1, 5, 7) AND kind = 0");

        // Prompt payload. Sensitive members are encrypted before this JSON is stored.
        builder.Property(x => x.FormValues).HasColumnType("jsonb");

        // WP3 — encrypted resume checkpoint, non-null only while Paused. jsonb-free
        // (the payload is a DEK-encrypted opaque string, not queryable JSON).
        builder.Property(x => x.PauseCheckpointEncrypted);

        builder.Property(x => x.DropBundlePath).HasMaxLength(500);

        // ── Provenance (fix 6) ───────────────────────────────────────────────
        // Real FK to users with SET NULL: a task is not user-OWNED (unlike api_keys,
        // which CASCADE) — it is only user-INITIATED, so its execution history must
        // survive the initiator's deletion. users is account-global (not Space-scoped),
        // so this is a simple single-column FK like fk_api_keys_users_user_id. No
        // navigation on the domain entity (house convention keeps domain→Identity refs
        // as bare Guids). The denormalized created_by_display keeps provenance readable
        // after the id is nulled.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.CreatedByUserId)
            .HasFilter("created_by_user_id IS NOT NULL");

        builder.Property(x => x.CreatedByDisplay)
            .IsRequired()
            .HasMaxLength(TaskInitiator.MaxDisplayLength);
        builder.Property(x => x.Cause).IsRequired().HasConversion<int>();
        builder.Property(x => x.CauseDetail).HasMaxLength(TaskInitiator.MaxDetailLength);

        // Composite Space FK: a task's environment must be in the task's Space.
        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.EnvironmentId })
            .HasPrincipalKey(e => new { e.SpaceId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Composite Space FK to the (optional) tenant. SetNull on a composite FK
        // whose space_id is NOT NULL needs the Postgres column-list form
        // `ON DELETE SET NULL (tenant_id)` (rewritten with raw SQL in the migration).
        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.TenantId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.SetNull);

        // Parent-task link — set when an Octopus.DeployRelease step triggered this
        // task. SetNull on delete so deleting a parent doesn't cascade away the
        // child's history. Composite self-FK: parent must be in the same Space
        // (raw-SQL column-list SET NULL in the migration, same reason as tenant_id).
        builder.HasOne(x => x.ParentTask)
            .WithMany()
            .HasForeignKey(x => new { x.SpaceId, x.ParentTaskId })
            .HasPrincipalKey(t => new { t.SpaceId, t.Id })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.ParentTaskId)
            .HasFilter("parent_task_id IS NOT NULL");

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

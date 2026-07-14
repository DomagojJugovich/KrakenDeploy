using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Core.Tests;

public sealed class SpacesTests
{
    // ── WellKnown.DefaultSpaceId ───────────────────────────────────────────────

    [Fact]
    public void DefaultSpaceId_is_a_non_zero_fixed_guid()
    {
        // Stability matters — the AddSpacesFoundation migration hard-codes this
        // exact value to backfill space_id on every existing row. Changing it
        // without a migration would orphan all on-prem data.
        WellKnown.DefaultSpaceId.Should().Be(new Guid("00000000-0000-0000-0000-00000000d543"));
        WellKnown.DefaultSpaceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void DefaultSpaceSlug_and_Name_are_stable()
    {
        WellKnown.DefaultSpaceSlug.Should().Be("default");
        WellKnown.DefaultSpaceName.Should().Be("Default");
    }

    // ── Marker interface coverage ──────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(Project))]
    [InlineData(typeof(DeploymentTarget))]
    [InlineData(typeof(Tenant))]
    public void Top_level_aggregates_implement_ISpaceScoped(Type entityType)
    {
        // The query filter and the migration generator both rely on this marker
        // interface to identify space-scoped entities. If a top-level aggregate
        // stops implementing it, it falls out of the global filter and starts
        // leaking across Spaces — a correctness bug, not a perf issue.
        typeof(ISpaceScoped).IsAssignableFrom(entityType)
            .Should().BeTrue($"{entityType.Name} is a top-level aggregate and must be space-scoped.");
    }

    [Fact]
    public void All_top_level_aggregates_in_assembly_implement_ISpaceScoped()
    {
        // Sanity check — anything else AuditableEntity-derived that lives in
        // its own top-level Domain folder should be ISpaceScoped. Child
        // entities (Variable, DeploymentLogEntry, RunbookRunLogEntry, etc.)
        // reach a Space transitively via a parent FK and are explicitly
        // excluded here.
        var excluded = new HashSet<string>(StringComparer.Ordinal)
        {
            // Children still reached only via a Space-scoped parent navigation.
            // The cross-space IDOR remediation promoted the directly-queried /
            // agent-written children to ISpaceScoped (process/step/variable/tag
            // in tier 1; deployment logs/outcomes/output-vars/artifacts, runbook
            // runs + their logs, and adhoc iterations in tier 2 — those set
            // SpaceId explicitly from the parent at each agent/transport write
            // site, since that path has no real Space context). The entries
            // below remain genuinely transitive: read only via a Space-scoped
            // parent's navigation, never queried/mutated directly by id/FK.
            // Join row between a task and its targets. Scope inherits via TaskId.
            "TaskTargetAssignment",
            // Hybrid task-log tables (staging + compacted blob). Deliberately NOT
            // ISpaceScoped — scope inherits via TaskId; kept off the highest-volume
            // tables so they carry no redundant space_id column/index. Every read
            // resolves the parent task under the Space filter first.
            "TaskLogLiveEntry",
            "TaskStepLog",
            "LifecyclePhase",
            "StepSnapshot",
            "StepTemplateParameter",
            // Space itself — it's the partition, not a member of one
            "Space",
            // Common base classes
            "Entity",
            "AuditableEntity",
            // Value objects on Targets
            "OfflineDropConfig",
            // M10 RBAC — system-level (Role, IdentityProvider) or nullable-
            // SpaceId pattern (Team, RoleAssignment) so visible across Spaces
            "Role",
            "Team",
            "TeamMember",
            "TeamExternalGroup",
            "RoleAssignment",
            "IdentityProvider",
            // System-wide GitHub community-template cache — refreshed by a
            // server-side poller; not partitioned per Space.
            "StepTemplateCatalogEntry",
            // System-wide step-package install registry (Phase D — .kdeploy-step
            // plugins). Packages are platform-level: a Kraken admin manages them
            // centrally, not per Space.
            "StepPackage",
            // System-wide step-package catalog cache (Phase D-9) — Hangfire job
            // mirrors a public GitHub feed; same platform-level scope as
            // StepPackage itself.
            "StepPackageCatalogEntry",
            // Server-wide backup history (M13.G) — one backup policy per
            // instance; runs are server-level audit-like rows. (BackupSettings,
            // SmtpSettings, FeatureFlag, MaintenanceSettings, PerformanceSettings
            // are no longer aggregates — they were folded into the unified
            // `settings` table as ISettingsDocument POCOs, so they no longer
            // derive from AuditableEntity/Entity and drop out of this scan.)
            "BackupRun",
            // Subscriptions (M13.B.2/3) — nullable-SpaceId pattern (like
            // Team / RoleAssignment) instead of the ISpaceScoped marker
            // because a single row can be either Space-scoped or
            // system-wide (SpaceId=null).
            "EventSubscription",
            "SubscriptionDelivery",
            // Singleton row for the outbox-poller cursor (M13.B.2/3
            // Phase 2). Server-wide; not partitioned per Space.
            "SubscriptionPollerState",
            // Digest-outbox buffer (M13.B.2/3 Phase 5). References a
            // subscription which carries its own SpaceId; outbox rows
            // inherit scope transitively via SubscriptionId.
            "EmailDigestOutboxEntry",
            // Per-user API keys (M13.C.4) — platform-level rows keyed by
            // owning user (like AuditEntry). SpaceId is an OPTIONAL
            // restriction column, not a tenancy partition, so the entity is
            // deliberately not ISpaceScoped (an ISpaceScoped ApiKey would be
            // hidden by the global Space filter at auth time — wrong).
            "ApiKey",
            // Wrapped data-encryption key (M13.D.2) — platform-level crypto
            // material, one instance-wide row; never Space-partitioned.
            "DataEncryptionKey",
            // Unified settings table (fix 7) — deliberately NOT ISpaceScoped
            // (nullable scope discriminator; Space caging lives in
            // SettingsService, enforced by an architecture test). Holds System-
            // and Space-scoped documents in one table.
            "Setting",
        };

        var assembly = typeof(Project).Assembly;
        var topLevel = assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("KrakenDeploy.Server.Core.Domain", StringComparison.Ordinal) == true)
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.BaseType is { Name: "AuditableEntity" or "Entity" })
            .Where(t => !excluded.Contains(t.Name))
            .ToList();

        var unscoped = topLevel
            .Where(t => !typeof(ISpaceScoped).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        unscoped.Should().BeEmpty(
            "every top-level aggregate must be space-scoped or explicitly excluded in this test");
    }

    [Fact]
    public void Setting_SpaceId_on_aggregate_is_persisted_on_the_property()
    {
        // Smoke-test that the property assignment actually works (could be
        // accidentally implemented as init-only or otherwise locked down).
        var customSpace = Guid.NewGuid();
        var project = new Project { Slug = "p", Name = "P" };
        ((ISpaceScoped)project).SpaceId = customSpace;

        project.SpaceId.Should().Be(customSpace);
    }
}

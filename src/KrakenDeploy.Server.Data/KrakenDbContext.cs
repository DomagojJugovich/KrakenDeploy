using System.Linq.Expressions;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Configurations;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data;

public class KrakenDbContext(
    DbContextOptions<KrakenDbContext> options,
    ISpaceContext spaceContext,
    IAccountContext? accountContext = null)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    // We use IdentityUserContext (not IdentityDbContext) because we have our
    // own RBAC model (Role + Team + RoleAssignment in Server.Core.Domain.Security)
    // — Identity-managed roles via IdentityRole<Guid> would clash with our
    // domain Role and add a parallel permission system we don't use.
    private readonly ISpaceContext _spaceContext = spaceContext;

    // Non-null only in SaaS multi-account mode (injected by the Scoped factory from
    // the request/operation scope). When present, the tenant connection is taken
    // from the resolved account in OnConfiguring, so a context binds to the active
    // account's database. Null for single-instance installs, CLI, migrations, and
    // tests, which use the connection baked into the options.
    private readonly IAccountContext? _accountContext = accountContext;

    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectGroup> ProjectGroups => Set<ProjectGroup>();
    public DbSet<DeploymentEnvironment> Environments => Set<DeploymentEnvironment>();
    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();
    // Explicit Space-scoped join entities (replace the former implicit EF joins).
    public DbSet<KrakenDeploy.Server.Core.Domain.Targets.TargetTenant> TargetTenants
        => Set<KrakenDeploy.Server.Core.Domain.Targets.TargetTenant>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Targets.TargetEnvironment> TargetEnvironments
        => Set<KrakenDeploy.Server.Core.Domain.Targets.TargetEnvironment>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Projects.ProjectTenant> ProjectTenants
        => Set<KrakenDeploy.Server.Core.Domain.Projects.ProjectTenant>();
    public DbSet<Release> Releases => Set<Release>();
    // Unified execution spine (server_tasks, TPH). ServerTasks is the base set;
    // Deployments / RunbookRuns are the discriminator-filtered typed surfaces.
    public DbSet<ServerTask> ServerTasks => Set<ServerTask>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Package> Packages => Set<Package>();

    // Unified process shape (one processes + one process_steps table).
    public DbSet<Process> Processes => Set<Process>();
    public DbSet<ProcessStep> ProcessSteps => Set<ProcessStep>();

    // Unified task children (FK task_id).
    public DbSet<TaskLogLiveEntry> TaskLogLive => Set<TaskLogLiveEntry>();
    public DbSet<TaskLogCounter> TaskLogCounters => Set<TaskLogCounter>();
    public DbSet<TaskStepLog> TaskStepLogs => Set<TaskStepLog>();
    public DbSet<TaskArtifact> TaskArtifacts => Set<TaskArtifact>();
    public DbSet<TaskOutputVariable> TaskOutputVariables => Set<TaskOutputVariable>();
    public DbSet<TaskStepOutcome> TaskStepOutcomes => Set<TaskStepOutcome>();
    public DbSet<TaskTargetAssignment> TaskTargetAssignments => Set<TaskTargetAssignment>();
    // WP3 — manual-intervention gates (one per gating step per task).
    public DbSet<Interruption> Interruptions => Set<Interruption>();
    public DbSet<VariableSet> VariableSets => Set<VariableSet>();
    public DbSet<Variable> Variables => Set<Variable>();
    public DbSet<ProjectVariableSetLink> ProjectVariableSetLinks => Set<ProjectVariableSetLink>();
    public DbSet<StepTemplate> StepTemplates => Set<StepTemplate>();
    public DbSet<StepTemplateCatalogEntry> StepTemplateCatalog => Set<StepTemplateCatalogEntry>();
    public DbSet<StepPackage> StepPackages => Set<StepPackage>();
    public DbSet<StepPackageCatalogEntry> StepPackageCatalog => Set<StepPackageCatalogEntry>();
    public DbSet<StepPackageSchema> StepPackageSchemas => Set<StepPackageSchema>();
    public DbSet<StepTypeEntry> StepTypes => Set<StepTypeEntry>();
    public DbSet<AiCallLog> AiCallLogs => Set<AiCallLog>();
    public DbSet<DeploymentDiagnosis> DeploymentDiagnoses => Set<DeploymentDiagnosis>();
    public DbSet<AdhocSession> AdhocSessions => Set<AdhocSession>();
    public DbSet<AdhocIteration> AdhocIterations => Set<AdhocIteration>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Freezes.DeploymentFreeze> DeploymentFreezes
        => Set<KrakenDeploy.Server.Core.Domain.Freezes.DeploymentFreeze>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Backup.BackupRun> BackupRuns
        => Set<KrakenDeploy.Server.Core.Domain.Backup.BackupRun>();
    // SmtpSettings, BackupSettings, MaintenanceSettings, PerformanceSettings,
    // FeatureFlag, and SpaceAiSettings are folded into the unified `settings`
    // table. There is deliberately NO settings DbSet here: SettingsService is the
    // sole accessor (it reads the settings entity internally), enforced by an
    // architecture test, so the nullable-scope table can't be queried unscoped.
    public DbSet<KrakenDeploy.Server.Core.Domain.Subscriptions.EventSubscription> EventSubscriptions
        => Set<KrakenDeploy.Server.Core.Domain.Subscriptions.EventSubscription>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Subscriptions.SubscriptionDelivery> SubscriptionDeliveries
        => Set<KrakenDeploy.Server.Core.Domain.Subscriptions.SubscriptionDelivery>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Analytics.PivotView> PivotViews
        => Set<KrakenDeploy.Server.Core.Domain.Analytics.PivotView>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Projects.ProjectDashboardView> ProjectDashboardViews
        => Set<KrakenDeploy.Server.Core.Domain.Projects.ProjectDashboardView>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Dashboards.DashboardLayout> DashboardLayouts
        => Set<KrakenDeploy.Server.Core.Domain.Dashboards.DashboardLayout>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Subscriptions.SubscriptionPollerState> SubscriptionPollerStates
        => Set<KrakenDeploy.Server.Core.Domain.Subscriptions.SubscriptionPollerState>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Subscriptions.EmailDigestOutboxEntry> EmailDigestOutbox
        => Set<KrakenDeploy.Server.Core.Domain.Subscriptions.EmailDigestOutboxEntry>();
    public DbSet<Tenant> Tenants => Set<Tenant>();

    // ── Extended tag sets (Space-level; see docs/extended-tag-sets-plan.md) ──
    public DbSet<KrakenDeploy.Server.Core.Domain.Tags.TagSet> TagSets
        => Set<KrakenDeploy.Server.Core.Domain.Tags.TagSet>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Tags.Tag> Tags
        => Set<KrakenDeploy.Server.Core.Domain.Tags.Tag>();
    public DbSet<KrakenDeploy.Server.Core.Domain.Tags.TagApplication> TagApplications
        => Set<KrakenDeploy.Server.Core.Domain.Tags.TagApplication>();
    public DbSet<Lifecycle> Lifecycles => Set<Lifecycle>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Runbook> Runbooks => Set<Runbook>();
    public DbSet<RunbookRun> RunbookRuns => Set<RunbookRun>();

    // ── Audit log ────────────────────────────────────────────────────────────
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // ── M10 RBAC ────────────────────────────────────────────────────────────
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamExternalGroup> TeamExternalGroups => Set<TeamExternalGroup>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<RoleAssignmentScope> RoleAssignmentScopes => Set<RoleAssignmentScope>();
    public DbSet<IdentityProvider> IdentityProviders => Set<IdentityProvider>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<DataEncryptionKey> DataEncryptionKeys => Set<DataEncryptionKey>();

    /// <summary>
    /// Read by the EF Core global query filter for every <see cref="ISpaceScoped"/>
    /// entity. EF treats this as a parameter and re-evaluates per query, so changing
    /// the active Space (e.g. via <c>ISpaceContext.WithSpace</c>) takes effect
    /// immediately on the next query — no model rebuild required.
    /// </summary>
    public Guid CurrentSpaceId => _spaceContext.CurrentSpaceId;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // SaaS multi-account: bind to the active account's tenant database. The
        // factory is Scoped and injects the request/operation-scoped IAccountContext,
        // so this resolves the right tenant per request. Returns null in single-
        // instance mode (no override); throws if multi-account is on but no account is
        // resolved (cross-customer boundary — fail closed). The naming convention +
        // interceptors configured on the factory options are preserved; only the
        // connection is overridden.
        var tenantConnectionString = _accountContext?.ResolveTenantConnectionString();
        if (tenantConnectionString is not null)
        {
            optionsBuilder.UseNpgsql(tenantConnectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ConfigureIdentity();
        builder.ApplyConfigurationsFromAssembly(typeof(KrakenDbContext).Assembly);

        ApplySpaceQueryFilters(builder);
    }

    /// <summary>
    /// Applies an EF Core global query filter to every entity that implements
    /// <see cref="ISpaceScoped"/>: <c>e =&gt; e.SpaceId == CurrentSpaceId</c>.
    /// Use <c>IQueryable&lt;T&gt;.IgnoreQueryFilters()</c> to bypass for admin /
    /// cross-Space queries.
    /// </summary>
    private void ApplySpaceQueryFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (!typeof(ISpaceScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            // TPH: a query filter may only be defined on the ROOT of an
            // inheritance hierarchy — EF throws if applied to a derived type.
            // ServerTask (the ISpaceScoped root) carries the filter for its
            // Deployment / RunbookRun derived types automatically.
            if (entityType.BaseType is not null)
            {
                continue;
            }

            // Build: e => e.SpaceId == this.CurrentSpaceId
            var param = Expression.Parameter(entityType.ClrType, "e");
            var spaceIdProp = Expression.Property(param, nameof(ISpaceScoped.SpaceId));
            var contextProp = Expression.Property(
                Expression.Constant(this),
                nameof(CurrentSpaceId));
            var body = Expression.Equal(spaceIdProp, contextProp);
            var lambda = Expression.Lambda(body, param);

            builder.Entity(entityType.ClrType).HasQueryFilter(lambda);
        }
    }
}

using System.Linq.Expressions;
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
    ISpaceContext spaceContext)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    // We use IdentityUserContext (not IdentityDbContext) because we have our
    // own RBAC model (Role + Team + RoleAssignment in Server.Core.Domain.Security)
    // — Identity-managed roles via IdentityRole<Guid> would clash with our
    // domain Role and add a parallel permission system we don't use.
    private readonly ISpaceContext _spaceContext = spaceContext;

    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectGroup> ProjectGroups => Set<ProjectGroup>();
    public DbSet<DeploymentEnvironment> Environments => Set<DeploymentEnvironment>();
    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<DeploymentProcess> DeploymentProcesses => Set<DeploymentProcess>();
    public DbSet<DeploymentStep> DeploymentSteps => Set<DeploymentStep>();
    public DbSet<DeploymentLogEntry> DeploymentLogEntries => Set<DeploymentLogEntry>();
    public DbSet<DeploymentArtifact> DeploymentArtifacts => Set<DeploymentArtifact>();
    public DbSet<DeploymentOutputVariable> DeploymentOutputVariables => Set<DeploymentOutputVariable>();
    public DbSet<VariableSet> VariableSets => Set<VariableSet>();
    public DbSet<Variable> Variables => Set<Variable>();
    public DbSet<StepTemplate> StepTemplates => Set<StepTemplate>();
    public DbSet<StepTemplateCatalogEntry> StepTemplateCatalog => Set<StepTemplateCatalogEntry>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TagSet> TagSets => Set<TagSet>();
    public DbSet<TenantTag> TenantTags => Set<TenantTag>();
    public DbSet<Lifecycle> Lifecycles => Set<Lifecycle>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Runbook> Runbooks => Set<Runbook>();
    public DbSet<RunbookProcess> RunbookProcesses => Set<RunbookProcess>();
    public DbSet<RunbookStep> RunbookSteps => Set<RunbookStep>();
    public DbSet<RunbookRun> RunbookRuns => Set<RunbookRun>();
    public DbSet<RunbookRunLogEntry> RunbookRunLogEntries => Set<RunbookRunLogEntry>();

    // ── Audit log ────────────────────────────────────────────────────────────
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // ── M10 RBAC ────────────────────────────────────────────────────────────
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamExternalGroup> TeamExternalGroups => Set<TeamExternalGroup>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<IdentityProvider> IdentityProviders => Set<IdentityProvider>();

    /// <summary>
    /// Read by the EF Core global query filter for every <see cref="ISpaceScoped"/>
    /// entity. EF treats this as a parameter and re-evaluates per query, so changing
    /// the active Space (e.g. via <c>ISpaceContext.WithSpace</c>) takes effect
    /// immediately on the next query — no model rebuild required.
    /// </summary>
    public Guid CurrentSpaceId => _spaceContext.CurrentSpaceId;

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

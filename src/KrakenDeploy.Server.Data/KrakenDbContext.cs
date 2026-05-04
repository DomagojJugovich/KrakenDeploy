using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Runbooks;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Configurations;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data;

public class KrakenDbContext(DbContextOptions<KrakenDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DeploymentEnvironment> Environments => Set<DeploymentEnvironment>();
    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<DeploymentProcess> DeploymentProcesses => Set<DeploymentProcess>();
    public DbSet<DeploymentStep> DeploymentSteps => Set<DeploymentStep>();
    public DbSet<DeploymentLogEntry> DeploymentLogEntries => Set<DeploymentLogEntry>();
    public DbSet<DeploymentArtifact> DeploymentArtifacts => Set<DeploymentArtifact>();
    public DbSet<VariableSet> VariableSets => Set<VariableSet>();
    public DbSet<Variable> Variables => Set<Variable>();
    public DbSet<StepTemplate> StepTemplates => Set<StepTemplate>();
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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ConfigureIdentity();
        builder.ApplyConfigurationsFromAssembly(typeof(KrakenDbContext).Assembly);
    }
}

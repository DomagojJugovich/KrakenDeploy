using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

public class Deployment : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;
    public Guid? TargetId { get; set; }
    public DeploymentTarget? Target { get; set; }

    /// <summary>
    /// M-RollingDeployments groundwork — the deployment's target SET.
    /// Pre-this-milestone every deployment had exactly one target (via
    /// <see cref="TargetId"/>); the join entity lifts that constraint
    /// so multi-target deployments become possible. The upgrade
    /// migration backfills one row per existing
    /// <see cref="TargetId"/>; new code can populate the collection
    /// for multi-target dispatch when the orchestrator rewrite lands.
    /// <para>
    /// During the transition (this commit), the legacy
    /// <see cref="TargetId"/> + <see cref="Target"/> nav stay the
    /// source of truth for the orchestrator + every existing
    /// per-target code path. Reading from the join collection is
    /// safe but not yet exercised on the dispatch hot path.
    /// </para>
    /// </summary>
    public ICollection<DeploymentTargetAssignment> Targets { get; set; } = [];
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Log lines written by the agent during execution.</summary>
    public ICollection<DeploymentLogEntry> LogEntries { get; set; } = [];

    /// <summary>Files collected from the agent at the end of each step.</summary>
    public ICollection<DeploymentArtifact> Artifacts { get; set; } = [];

    /// <summary>
    /// Tracks the next sequence number for log entries.
    /// Incremented atomically in the hub to guarantee ordering under concurrency.
    /// </summary>
    public int NextLogSequence { get; set; }

    /// <summary>
    /// Relative path to the drop bundle zip for offline-drop deployments.
    /// Null for agent-dispatched deployments. Set by <c>DropBundleService</c>.
    /// </summary>
    public string? DropBundlePath { get; set; }

    /// <summary>
    /// When set, the deployment is held in <c>Queued</c> state until this
    /// point in time. <c>null</c> means dispatch immediately on creation.
    /// The Hangfire <c>ScheduledDeploymentDispatchJob</c> polls for due entries.
    /// </summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>
    /// When this deployment was triggered by an <c>Octopus.DeployRelease</c>
    /// step in another deployment, this is that parent deployment's id.
    /// <c>null</c> for top-level deployments. Used for the audit trail and to
    /// power "show child deployments triggered by this one" UI surfaces.
    /// </summary>
    public Guid? ParentDeploymentId { get; set; }

    /// <summary>
    /// Navigation: the parent deployment when <see cref="ParentDeploymentId"/>
    /// is set. EF lazy-loads on demand; not eagerly included by default.
    /// </summary>
    public Deployment? ParentDeployment { get; set; }
}

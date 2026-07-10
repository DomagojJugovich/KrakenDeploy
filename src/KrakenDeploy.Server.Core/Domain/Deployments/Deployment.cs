using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

public class Deployment : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public Guid ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;

    /// <summary>
    /// The deployment's target SET — the single authority for "which
    /// targets does this deployment hit". Exactly one row for classic
    /// single-target deployments, N rows for rolling/parallel fan-out.
    /// (The transitional <c>TargetId</c> column that used to duplicate the
    /// first assignment was dropped in the 2026-07 schema hardening;
    /// rows are ordered by <see cref="DeploymentTargetAssignment.AddedUtc"/>,
    /// so the first-assigned target is the canonical one where a single
    /// representative is needed, e.g. server-wave machine variables.)
    /// </summary>
    public ICollection<DeploymentTargetAssignment> Targets { get; set; } = [];
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;

    /// <summary>
    /// How the rolling orchestrator reacts when a target fails a Required step.
    /// Defaults to <see cref="DeploymentFailureMode.BestEffort"/>.
    /// </summary>
    public DeploymentFailureMode FailureMode { get; set; } = DeploymentFailureMode.BestEffort;

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

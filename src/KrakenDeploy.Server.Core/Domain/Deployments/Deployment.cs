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
}

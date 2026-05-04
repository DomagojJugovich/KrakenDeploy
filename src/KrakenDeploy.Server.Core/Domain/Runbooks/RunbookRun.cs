using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Tenants;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// One execution of a <see cref="Runbook"/>. The runbook's process is snapped into
/// <see cref="ProcessSnapshot"/> at dispatch time so historical runs remain accurate
/// even after the runbook's steps are edited.
/// The agent receives this run as a <see cref="Contracts.DeploymentPlan"/> with the
/// RunbookRun ID in the <c>DeploymentId</c> field; the hub resolves it back here.
/// </summary>
public class RunbookRun : AuditableEntity
{
    public Guid RunbookId { get; set; }
    public Runbook Runbook { get; set; } = null!;

    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;

    public Guid? TargetId { get; set; }
    public DeploymentTarget? Target { get; set; }

    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Snapshot of the runbook process taken at trigger time.</summary>
    public List<StepSnapshot> ProcessSnapshot { get; set; } = [];

    /// <summary>Incremented atomically for ordering log entries.</summary>
    public int NextLogSequence { get; set; }

    public ICollection<RunbookRunLogEntry> LogEntries { get; set; } = [];
}

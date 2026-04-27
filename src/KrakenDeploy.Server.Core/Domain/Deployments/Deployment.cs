using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Targets;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

public class Deployment : AuditableEntity
{
    public Guid ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;
    public Guid? TargetId { get; set; }
    public DeploymentTarget? Target { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
}

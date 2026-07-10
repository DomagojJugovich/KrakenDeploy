using KrakenDeploy.Server.Core.Domain.Releases;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// A release deployed into an environment — a <see cref="ServerTask"/> of kind
/// <see cref="ServerTaskKind.Deployment"/>. Adds the deployment-specific
/// <see cref="ReleaseId"/>; everything else (targets, status, failure mode,
/// scheduling, log sequence, children) lives on the shared spine.
/// </summary>
public sealed class Deployment : ServerTask
{
    public Deployment() => Kind = ServerTaskKind.Deployment;

    public Guid ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
}

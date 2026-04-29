using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// The deployment process for a project — an ordered list of steps that the
/// agent executes whenever a release is deployed. One-to-one with <see cref="Project"/>.
/// </summary>
public class DeploymentProcess : Entity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public ICollection<DeploymentStep> Steps { get; set; } = [];
}

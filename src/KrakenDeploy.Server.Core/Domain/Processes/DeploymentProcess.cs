using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;

namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// The deployment process for a project — an ordered list of steps that the
/// agent executes whenever a release is deployed. One-to-one with <see cref="Project"/>.
/// </summary>
public class DeploymentProcess : Entity, ISpaceScoped
{
    /// <summary>Inherited from the owning Project; stamped on insert and
    /// backfilled for existing rows so by-id/projectId reads are Space-safe.</summary>
    public Guid SpaceId { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public ICollection<DeploymentStep> Steps { get; set; } = [];
}

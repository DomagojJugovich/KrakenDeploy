using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Core.Domain.Projects;

public class Project : AuditableEntity
{
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Deployment process for this project (one-to-one, created lazily).</summary>
    public DeploymentProcess? Process { get; set; }
}

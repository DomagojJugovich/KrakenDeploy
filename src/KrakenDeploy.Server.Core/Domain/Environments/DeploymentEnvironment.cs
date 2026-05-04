using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Environments;

public class DeploymentEnvironment : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public required string Slug { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
}

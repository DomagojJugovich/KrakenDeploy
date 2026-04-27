using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Projects;

public class Project : AuditableEntity
{
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}

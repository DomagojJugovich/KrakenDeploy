namespace KrakenDeploy.Server.Core.Domain.Common;

public abstract class AuditableEntity : Entity, IAuditable
{
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ModifiedUtc { get; set; }
}

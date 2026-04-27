namespace KrakenDeploy.Server.Core.Domain.Common;

public interface IAuditable
{
    DateTimeOffset CreatedUtc { get; set; }
    DateTimeOffset? ModifiedUtc { get; set; }
}

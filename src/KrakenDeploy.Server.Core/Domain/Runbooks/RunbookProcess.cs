using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// The ordered set of steps that make up a <see cref="Runbook"/>.
/// One-to-one with its runbook; created lazily on first step addition.
/// </summary>
public class RunbookProcess : Entity
{
    public Guid RunbookId { get; set; }
    public Runbook Runbook { get; set; } = null!;

    public ICollection<RunbookStep> Steps { get; set; } = [];
}

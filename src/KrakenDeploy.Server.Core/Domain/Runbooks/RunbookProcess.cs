using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Runbooks;

/// <summary>
/// The ordered set of steps that make up a <see cref="Runbook"/>.
/// One-to-one with its runbook; created lazily on first step addition.
/// </summary>
public class RunbookProcess : Entity, ISpaceScoped
{
    /// <summary>Inherited from the owning Runbook; stamped on insert and
    /// backfilled for existing rows so by-id/runbookId reads are Space-safe.</summary>
    public Guid SpaceId { get; set; }

    public Guid RunbookId { get; set; }
    public Runbook Runbook { get; set; } = null!;

    public ICollection<RunbookStep> Steps { get; set; } = [];
}

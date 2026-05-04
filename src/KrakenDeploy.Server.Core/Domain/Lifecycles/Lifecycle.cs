using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Lifecycles;

/// <summary>
/// A lifecycle defines the ordered sequence of environments (grouped into phases) that a
/// release must pass through on its way to production. Channels reference a lifecycle;
/// the lifecycle gate is enforced on every <c>POST /api/deployments</c> call.
/// </summary>
public class Lifecycle : AuditableEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Ordered phases. Stored as JSONB — phases are edited as a whole document.
    /// </summary>
    public List<LifecyclePhase> Phases { get; set; } = [];
}

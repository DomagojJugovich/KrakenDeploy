using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Analytics;

/// <summary>
/// A named, per-user saved pivot layout for the dashboard analytics table:
/// which fields sit in rows / columns / values and with which aggregate
/// functions. The layout is stored as JSON (<see cref="Definition"/>) so the
/// schema doesn't change every time the analytics UI grows a knob — the
/// shape is owned by the UI layer (PivotLayout record). Space-scoped because
/// the layout references the space's facts; also private to its owning user.
/// </summary>
public class PivotView : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>Owner. Views are private to the user who saved them.</summary>
    public Guid UserId { get; set; }

    /// <summary>Display name, unique per user ("Failures by tenant").</summary>
    public required string Name { get; set; }

    /// <summary>JSON-serialized pivot layout.</summary>
    public required string Definition { get; set; }
}

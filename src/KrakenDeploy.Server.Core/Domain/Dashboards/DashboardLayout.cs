using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Dashboards;

/// <summary>
/// A per-user saved arrangement of a dashboard's tiles (position + size on the
/// Radzen tile-layout grid). One row per user per dashboard (see
/// <see cref="DashboardKey"/>) — the user's personal layout. The arrangement is
/// stored as JSON (<see cref="Definition"/>) so the schema doesn't churn as tiles
/// are added or the layout model grows; the shape is owned by the UI layer.
/// </summary>
public class DashboardLayout : AuditableEntity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    /// <summary>Owner. The saved layout is private to this user.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Which dashboard this layout belongs to (e.g. <c>"space-home"</c>). Lets the
    /// same table serve multiple dashboards later without a migration; part of the
    /// per-user unique key.
    /// </summary>
    public required string DashboardKey { get; set; }

    /// <summary>JSON-serialized tile arrangement (UI-owned shape).</summary>
    public required string Definition { get; set; }
}

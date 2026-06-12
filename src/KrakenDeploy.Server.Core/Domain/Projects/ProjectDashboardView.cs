using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Projects;

/// <summary>
/// A per-user saved default view of the Projects dashboard — which project
/// groups / projects / environments / tenants are shown (all, or only a
/// selected subset). One row per user (the user's default); the selection is
/// stored as JSON (<see cref="Definition"/>) so the schema doesn't churn as the
/// filter UI grows — the shape is owned by the UI layer (ProjectDashboardFilter).
/// </summary>
public class ProjectDashboardView : AuditableEntity
{
    /// <summary>Owner. The saved view is private to this user.</summary>
    public Guid UserId { get; set; }

    /// <summary>JSON-serialized <c>ProjectDashboardFilter</c>.</summary>
    public required string Definition { get; set; }
}

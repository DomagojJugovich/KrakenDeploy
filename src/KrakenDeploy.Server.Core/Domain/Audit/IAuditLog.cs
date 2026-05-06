namespace KrakenDeploy.Server.Core.Domain.Audit;

/// <summary>
/// Service for recording non-EF audit events (sign-in, sign-out, permission
/// denied, API usage, etc.).  EF entity changes are captured automatically
/// by <c>AuditLogInterceptor</c> without going through this interface.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Records a single audit event and persists it immediately.
    /// </summary>
    /// <param name="eventType">
    ///   Dot-separated event identifier — use a constant from
    ///   <see cref="AuditEventType"/> where possible.
    /// </param>
    /// <param name="subjectType">Entity type name, e.g. "Deployment".</param>
    /// <param name="subjectId">Entity primary key as string.</param>
    /// <param name="subjectName">Entity display name at event time.</param>
    /// <param name="details">Freeform detail text.</param>
    /// <param name="userId">
    ///   Override user ID. When null the current HTTP principal is used.
    /// </param>
    /// <param name="userDisplay">
    ///   Override user display name. When null the current HTTP principal's
    ///   Identity.Name is used.
    /// </param>
    Task RecordAsync(
        string eventType,
        string? subjectType  = null,
        string? subjectId    = null,
        string? subjectName  = null,
        string? details      = null,
        Guid?   userId       = null,
        string? userDisplay  = null,
        CancellationToken ct = default);
}

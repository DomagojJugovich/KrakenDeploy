namespace KrakenDeploy.Server.Core.Domain.Audit;

/// <summary>
/// Immutable record of a user or system action.
/// <para>
/// Written automatically by <c>AuditLogInterceptor</c> for every EF entity
/// change (Added / Modified / Deleted on <c>AuditableEntity</c> subclasses)
/// and explicitly via <see cref="IAuditLog.RecordAsync"/> for non-EF events
/// (sign-in, API calls, permission-denied, etc.).
/// </para>
/// </summary>
public class AuditEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Space the event occurred in; null for platform-level events.</summary>
    public Guid? SpaceId { get; set; }

    /// <summary>User who performed the action; null for system-generated events.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Human-readable display name captured at event time (email address or
    /// "System"). Denormalised so log entries remain readable after a user is
    /// deleted or renamed.
    /// </summary>
    public string UserDisplay { get; set; } = "System";

    /// <summary>UTC timestamp of the event.</summary>
    public DateTimeOffset OccurredUtc { get; set; }

    /// <summary>
    /// Dot-separated event type identifier, e.g. "Project.Created",
    /// "Deployment.Updated", "User.SignedIn".
    /// </summary>
    public string EventType { get; set; } = "";

    /// <summary>Entity type name, e.g. "Project", "DeploymentTarget".</summary>
    public string? SubjectType { get; set; }

    /// <summary>Entity primary key as a string.</summary>
    public string? SubjectId { get; set; }

    /// <summary>Entity display name at the time of the event.</summary>
    public string? SubjectName { get; set; }

    /// <summary>Requesting IP address (best-effort; null for background tasks).</summary>
    public string? IpAddress { get; set; }

    /// <summary>HTTP User-Agent header (best-effort).</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// JSONB snapshot of property values <em>before</em> a Modified or Deleted
    /// change. Null for Created events.
    /// </summary>
    public string? BeforeJson { get; set; }

    /// <summary>
    /// JSONB snapshot of property values <em>after</em> a Created or Modified
    /// change. Null for Deleted events.
    /// </summary>
    public string? AfterJson { get; set; }

    /// <summary>
    /// Freeform detail string for non-EF events.
    /// <para>
    /// Capped, and enforced by a truncating SETTER rather than by a check at each call
    /// site. This is the only string column on the entity that carries free text
    /// originating OUTSIDE the server — a handshake header, an agent's self-reported
    /// update outcome — and it is the one that leaves the premises: the subscription
    /// poller forwards audit rows to the webhook and e-mail transports, and the
    /// AI-inspect transport interpolates this exact value into an LLM prompt. Every
    /// sibling column already had <c>HasMaxLength</c>; this one did not, so it was
    /// Postgres <c>text</c> and one authenticated caller could push Kestrel's whole 32 KB
    /// header limit into a row, at whatever rate it liked. A setter is the only shape that
    /// cannot be bypassed: two call sites construct <see cref="AuditEntry"/> directly
    /// rather than going through <c>IAuditLog.RecordAsync</c>.
    /// </para>
    /// <para>
    /// EF materialisation writes the backing field, so reading a pre-cap row back is
    /// lossless; only application writes truncate.
    /// </para>
    /// </summary>
    public string? Details
    {
        get => _details;
        set => _details = value is { Length: > MaxDetailsLength }
            ? string.Concat(value.AsSpan(0, MaxDetailsLength - 1), "…")
            : value;
    }

    private string? _details;

    /// <summary>
    /// Cap on <see cref="Details"/>, matched by <c>AuditEntryConfiguration</c>'s
    /// <c>HasMaxLength</c> so the column and the setter cannot disagree. Generous for a
    /// real error message; far below what an abusive caller would like.
    /// </summary>
    public const int MaxDetailsLength = 4096;
}

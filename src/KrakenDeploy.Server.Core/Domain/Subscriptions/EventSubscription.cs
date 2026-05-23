using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Subscriptions;

/// <summary>
/// One row defines "when X happens in scope Y, deliver via transport Z".
/// Polled out of <c>audit_entries</c> by <c>SubscriptionPollerJob</c>:
/// every new audit row is matched against the active subscription set;
/// matches enqueue per-transport delivery work.
///
/// <para>
/// <b>Scope</b>: nullable <see cref="SpaceId"/> instead of the
/// <c>ISpaceScoped</c> marker so a single row can be either Space-scoped
/// (most common — operator subscribes to events in their Space) or
/// system-wide (<c>SpaceId = null</c>; only <c>AdministerSystem</c> can
/// create one; matches events across every Space). Same pattern <c>Team</c>
/// and <c>RoleAssignment</c> use.
/// </para>
///
/// <para>
/// <b>Filtering</b>: AND-of-dimensions, OR-within-dimension — same shape
/// Octopus uses. <see cref="EventTypePatterns"/> empty = "any event";
/// non-empty = event-type prefix or wildcard list (e.g. <c>"Deployment.*"</c>).
/// <see cref="ProjectIds"/> / <see cref="EnvironmentIds"/> empty = "any".
/// </para>
/// </summary>
public class EventSubscription : AuditableEntity
{
    /// <summary>Owning Space, or <c>null</c> for a system-wide subscription
    /// that matches events across every Space (sys-admin only).</summary>
    public Guid? SpaceId { get; set; }

    /// <summary>Operator-facing label (e.g. "Slack on prod failures").</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional human note. Doesn't enter the matching logic.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Event-type filter list. Each entry is either an exact event type
    /// (<c>Deployment.Failed</c>) or a category wildcard
    /// (<c>Deployment.*</c>). Empty list = match every event.
    /// Stored as a JSONB string array.
    /// </summary>
    public List<string> EventTypePatterns { get; set; } = [];

    /// <summary>Project filter (empty = any). Matched against the audit
    /// entry's SubjectId for Deployment / Release / Project events; the
    /// matcher walks the subject tree to find the owning project.
    /// Stored as JSONB.</summary>
    public List<Guid> ProjectIds { get; set; } = [];

    /// <summary>Environment filter (empty = any). Matched against the
    /// deployment's environment when applicable. Stored as JSONB.</summary>
    public List<Guid> EnvironmentIds { get; set; } = [];

    /// <summary>Pick which transport handles a match. The transport-
    /// specific configuration lives in <see cref="TransportConfigJson"/>
    /// — schema differs per transport (URL+secret for webhook,
    /// recipients list for email, runbook id for runbook trigger, prompt
    /// template for AI inspect).</summary>
    public SubscriptionTransport Transport { get; set; } = SubscriptionTransport.Webhook;

    /// <summary>Transport-specific config payload. Schema-on-read per
    /// <see cref="Transport"/>; the service layer validates on save and
    /// the transport layer deserialises at deliver time. JSONB column.</summary>
    public string TransportConfigJson { get; set; } = "{}";

    /// <summary>
    /// Email-transport-only knob. Zero = immediate per-event delivery.
    /// Positive = bucket events for N minutes and deliver a single
    /// digest email at the end of the window. Octopus parity (their
    /// digest cap is 100 events / message; we mirror that). Ignored by
    /// non-email transports.
    /// </summary>
    public int DigestEveryMinutes { get; set; }

    /// <summary>True = the poller skips this row. Lets operators draft
    /// a subscription + enable it later without deleting + recreating.</summary>
    public bool Disabled { get; set; }
}

/// <summary>Discriminator for <see cref="EventSubscription.Transport"/>.
/// Adding a transport: append a value here + register an
/// <c>IEventTransport</c> implementation + extend the UI's dialog
/// transport selector. The integer values are persisted so they MUST
/// stay stable.</summary>
public enum SubscriptionTransport
{
    /// <summary>HTTP POST with HMAC-signed JSON payload. Default — most
    /// flexible; consumers integrate at their own pace.</summary>
    Webhook = 0,

    /// <summary>SMTP email using <c>SmtpSettings</c> from M13.B.1.
    /// Honours <see cref="EventSubscription.DigestEveryMinutes"/>.</summary>
    Email = 1,

    /// <summary>Triggers a runbook via <c>RunbookService.TriggerAsync</c>.
    /// Event fields map into runbook prompted-variables. The
    /// differentiator KrakenDeploy has over stock Octopus parity.</summary>
    Runbook = 2,

    /// <summary>Calls <c>IKrakenAi</c> with the event payload + a prompt
    /// template; the model's response becomes a new audit event
    /// (<c>Diagnosis.Completed</c>). Closes M11.C — the "diagnose on
    /// Deployment.Failed" workflow becomes one specific subscription.</summary>
    AiInspect = 3,
}

using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Subscriptions;

/// <summary>
/// One row per (subscription × event) — the audit trail of what the router
/// delivered. The unique <c>(subscription_id, event_id)</c> index makes this
/// idempotent: a crash-resumed poll that re-processes the same audit row
/// cannot emit a second delivery. On transport failure the row is finalised
/// <see cref="SubscriptionDeliveryOutcome.Failed"/> in place — there is no
/// per-attempt retry row. Powers the per-subscription delivery-history
/// sub-grid in the UI ("did my Slack webhook receive the last 5 failures?").
///
/// <para>
/// Separate from <c>audit_entries</c> because the queries differ —
/// audit is "show me what happened in the world"; this is "show me what
/// I delivered to whom". A single Deployment.Failed event can produce
/// 3+ delivery rows (one webhook + two emails) and the operator wants to
/// see each independently.
/// </para>
/// </summary>
public class SubscriptionDelivery : Entity
{
    public Guid SubscriptionId { get; set; }

    /// <summary>FK to the <c>audit_entries</c> row that triggered this
    /// delivery. Lets the UI link the delivery back to the original
    /// event row.</summary>
    public Guid EventId { get; set; }

    public SubscriptionTransport Transport { get; set; }

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public TimeSpan? Duration { get; set; }

    public SubscriptionDeliveryOutcome Outcome { get; set; }

    /// <summary>Transport-supplied success blurb (e.g. HTTP status code +
    /// response banner; SMTP server response; runbook run id). Safe to
    /// surface in the UI.</summary>
    public string? Detail { get; set; }

    /// <summary>Verbatim exception / error message on failure. Surfaced
    /// in the UI; never contains secrets (transports redact before
    /// recording).</summary>
    public string? ErrorMessage { get; set; }
}

public enum SubscriptionDeliveryOutcome
{
    /// <summary>The row is in-flight — the transport hasn't returned yet.
    /// Visible to operators hitting the history page during a long
    /// HTTP timeout.</summary>
    InProgress = 0,

    Succeeded = 1,

    /// <summary>Transport returned an error; the row is finalised in place.
    /// Re-dispatch of the same (subscription, event) is blocked by the unique
    /// idempotency index rather than producing a follow-up row.</summary>
    Failed = 2,

    /// <summary>Operator killed the delivery before it ran (paused the
    /// subscription mid-flight, deleted it, etc.). Not the same as
    /// Failed — no retry should follow.</summary>
    Cancelled = 3,
}

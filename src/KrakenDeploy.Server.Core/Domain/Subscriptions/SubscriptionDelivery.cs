using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Subscriptions;

/// <summary>
/// One row per (subscription × event × transport-attempt) — the audit
/// trail of every delivery the router tried. Powers the per-subscription
/// delivery-history sub-grid in the UI ("did my Slack webhook actually
/// receive the last 5 failures?") and the Hangfire retry policy
/// (transient HTTP failure → row marked Failed → retry → new row marked
/// Succeeded).
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

    /// <summary>Attempt counter — 1 on first try, increments on Hangfire
    /// retry. The UI shows "(retry 2 of 3)" next to the row.</summary>
    public int AttemptNumber { get; set; } = 1;

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

    /// <summary>Transport returned an error. Hangfire's default retry
    /// policy may produce a follow-up row with a higher AttemptNumber.</summary>
    Failed = 2,

    /// <summary>Operator killed the delivery before it ran (paused the
    /// subscription mid-flight, deleted it, etc.). Not the same as
    /// Failed — no retry should follow.</summary>
    Cancelled = 3,
}

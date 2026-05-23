using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// Strategy interface bound by <see cref="SubscriptionTransport"/>. The
/// poller picks the implementation registered for the subscription's
/// declared transport and calls <see cref="DeliverAsync"/> once per match.
/// Implementations MUST NOT throw — they capture failures into
/// <see cref="EventTransportResult"/> so the poller can record the
/// delivery row + decide whether to retry without per-transport error
/// handling.
/// </summary>
public interface IEventTransport
{
    /// <summary>Which discriminator this transport handles. The poller
    /// dispatches by exact match.</summary>
    SubscriptionTransport Transport { get; }

    /// <summary>
    /// Deliver one event to one subscription. The caller has already
    /// matched the event + created the SubscriptionDelivery row in
    /// <see cref="SubscriptionDeliveryOutcome.InProgress"/> state and
    /// passes its id so the transport can attach success/failure detail
    /// when it finalises the row (or the caller does the finalisation —
    /// see the contract on the result record below).
    /// </summary>
    Task<EventTransportResult> DeliverAsync(
        EventSubscription subscription,
        AuditEntry auditEvent,
        CancellationToken ct);
}

/// <summary>
/// Mutually-exclusive success/failure result. <see cref="Detail"/> is a
/// transport-supplied success blurb (HTTP status + body, SMTP banner,
/// runbook-run id) safe to display in the UI; <see cref="Error"/> is
/// the exception message on failure.
/// </summary>
public sealed record EventTransportResult(
    bool Succeeded,
    string? Detail,
    string? Error)
{
    public static EventTransportResult Success(string detail)
        => new(true, detail, null);

    public static EventTransportResult Failure(string error)
        => new(false, null, error);
}

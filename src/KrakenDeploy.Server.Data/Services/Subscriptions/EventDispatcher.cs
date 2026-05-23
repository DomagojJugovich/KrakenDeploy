using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// Single entry point the poller calls per matched (subscription, event)
/// pair: create the in-flight delivery row, run the transport, finalise
/// the row + write an audit event for the final outcome.
///
/// <para>
/// Lives apart from the poller so the "Send test event" UI button (Phase
/// 4) and the digest flusher (Phase 5) can re-use it without lifting the
/// recurring-job into a service layer where it doesn't belong.
/// </para>
/// </summary>
public sealed class EventDispatcher(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IEnumerable<IEventTransport> transports,
    IAuditLog audit,
    ILogger<EventDispatcher> logger,
    TimeProvider time)
{
    private readonly Dictionary<SubscriptionTransport, IEventTransport> _byKind =
        transports.ToDictionary(t => t.Transport);

    /// <summary>
    /// Dispatches one event to one subscription. Idempotency is enforced
    /// by the UNIQUE (SubscriptionId, EventId) index on the delivery
    /// table — a re-run of the poller against the same row will fail
    /// the row insert and return without firing the transport again.
    /// </summary>
    public async Task<SubscriptionDelivery?> DispatchAsync(
        EventSubscription subscription, AuditEntry auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Email-digest mode bypasses the immediate transport — accumulate
        // into the outbox and let EmailDigestFlushJob batch + send when
        // the digest window elapses. Phase 5.
        if (subscription.Transport == SubscriptionTransport.Email &&
            subscription.DigestEveryMinutes > 0)
        {
            return await EnqueueDigestAsync(subscription, auditEvent, ct).ConfigureAwait(false);
        }

        if (!_byKind.TryGetValue(subscription.Transport, out var transport))
        {
            logger.LogWarning(
                "No transport registered for {Transport}; subscription {SubId} skipped.",
                subscription.Transport, subscription.Id);
            return null;
        }

        // Insert the in-flight row first. UNIQUE (subscription_id,
        // event_id) is the idempotency guard — a duplicate dispatch will
        // throw on save and we silently skip.
        var delivery = new SubscriptionDelivery
        {
            SubscriptionId = subscription.Id,
            EventId        = auditEvent.Id,
            Transport      = subscription.Transport,
            StartedUtc     = time.GetUtcNow(),
            Outcome        = SubscriptionDeliveryOutcome.InProgress,
            AttemptNumber  = 1,
        };
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.SubscriptionDeliveries.Add(delivery);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
            when (IsUniqueViolation(ex))
        {
            // Already delivered (or a concurrent dispatcher won the race).
            // No retry — the existing row carries the outcome.
            logger.LogDebug(
                "Duplicate dispatch skipped (sub {SubId}, event {EventId})",
                subscription.Id, auditEvent.Id);
            return null;
        }

        // Run the transport.
        var startedTimestamp = time.GetTimestamp();
        var result = await transport.DeliverAsync(subscription, auditEvent, ct).ConfigureAwait(false);
        var elapsed = time.GetElapsedTime(startedTimestamp);

        // Finalise the row.
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var tracked = await db.SubscriptionDeliveries
                .FirstAsync(d => d.Id == delivery.Id, ct)
                .ConfigureAwait(false);
            tracked.CompletedUtc = time.GetUtcNow();
            tracked.Duration     = elapsed;
            tracked.Outcome      = result.Succeeded
                ? SubscriptionDeliveryOutcome.Succeeded
                : SubscriptionDeliveryOutcome.Failed;
            tracked.Detail       = result.Detail;
            tracked.ErrorMessage = result.Error;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            delivery = tracked;
        }

        // Audit event so operators get one row per delivery in /audit too —
        // useful when a webhook is misbehaving and the operator wants to
        // grep for "Subscription.DeliveryFailed in the last hour".
        await audit.RecordAsync(
            eventType:   result.Succeeded
                            ? AuditEventType.SubscriptionDeliverySucceeded
                            : AuditEventType.SubscriptionDeliveryFailed,
            subjectType: "Subscription",
            subjectId:   subscription.Id.ToString(),
            subjectName: subscription.Name,
            details:     $"Transport={subscription.Transport}, Event={auditEvent.EventType}, " +
                         $"Elapsed={elapsed.TotalMilliseconds:F0}ms, " +
                         (result.Succeeded ? $"Detail={result.Detail}" : $"Error={result.Error}"),
            ct: ct).ConfigureAwait(false);

        return delivery;
    }

    /// <summary>
    /// Append one event to the digest outbox for a digest-mode email
    /// subscription. Returns a synthetic delivery record for the
    /// in-flight state so the page's history grid shows "in-progress"
    /// for the events waiting to be batched.
    /// </summary>
    private async Task<SubscriptionDelivery?> EnqueueDigestAsync(
        EventSubscription subscription, AuditEntry auditEvent, CancellationToken ct)
    {
        var entry = new EmailDigestOutboxEntry
        {
            SubscriptionId = subscription.Id,
            EventId        = auditEvent.Id,
            AddedUtc       = time.GetUtcNow(),
        };
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.EmailDigestOutbox.Add(entry);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogDebug(
                "Digest queued: sub={SubId} event={EventId} (window={Window}m)",
                subscription.Id, auditEvent.Id, subscription.DigestEveryMinutes);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Already queued — same idempotency contract as
            // SubscriptionDelivery's pre-insert.
            logger.LogDebug(
                "Duplicate digest queue skipped (sub {SubId}, event {EventId})",
                subscription.Id, auditEvent.Id);
        }
        // No SubscriptionDelivery row gets written here — the flusher
        // produces ONE delivery row per BATCH, not per event. Return null
        // so the poller logs "delivered=0" for this match (the actual
        // delivery happens in the flush cycle).
        return null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Npgsql's PostgresException exposes SqlState ("23505" = unique
        // violation). Reflection-read it so this layer doesn't need a
        // hard Npgsql package reference; also check the human-readable
        // message ("duplicate key value violates unique constraint") as
        // a belt-and-braces fallback across EF / driver versions.
        var current = (Exception?)ex;
        while (current is not null)
        {
            var sqlStateProp = current.GetType().GetProperty("SqlState");
            if (sqlStateProp?.GetValue(current) is string sqlState && sqlState == "23505")
            {
                return true;
            }
            if (current.Message.Contains("23505", StringComparison.Ordinal) ||
                current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}

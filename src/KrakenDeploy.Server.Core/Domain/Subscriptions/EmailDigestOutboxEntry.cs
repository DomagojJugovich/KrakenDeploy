using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Subscriptions;

/// <summary>
/// One row per (digest-Email subscription × matched event) waiting to be
/// included in the next digest email. Drained by
/// <c>EmailDigestFlushJob</c> when the subscription's digest window
/// elapses.
///
/// <para>
/// Lives separate from <see cref="SubscriptionDelivery"/> because the
/// digest-flush path produces ONE delivery row per BATCH (not per event)
/// — the outbox is the pre-batch buffer.
/// </para>
///
/// <para>
/// Octopus parity: digest emails cap at 100 events per message. The
/// flusher honours that cap — events beyond it stay in the outbox for
/// the next cycle.
/// </para>
/// </summary>
public class EmailDigestOutboxEntry : Entity
{
    public Guid SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
}

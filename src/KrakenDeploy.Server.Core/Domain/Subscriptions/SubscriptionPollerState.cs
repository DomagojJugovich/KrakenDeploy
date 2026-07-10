using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Subscriptions;

/// <summary>
/// Single-row table holding the poller's cursor — the latest
/// <see cref="Audit.AuditEntry.OccurredUtc"/> the
/// <c>SubscriptionPollerJob</c> has scanned. Survives restarts so the
/// poller resumes where it left off; idempotency against re-processed
/// events is enforced by the unique-per-(SubscriptionId, EventId)
/// invariant on <see cref="SubscriptionDelivery"/>.
/// <para>
/// Deliberately a plain <see cref="Entity"/>, NOT an
/// <see cref="AuditableEntity"/>: this is internal job bookkeeping, not
/// operator-facing config. An audited cursor advance would write an
/// <see cref="Audit.AuditEntry"/> on every poll cycle, and the poller
/// reads audit_entries as its own event source — so auditing it creates a
/// self-perpetuating churn loop (audit_entries grows ~1 row/minute even on
/// an idle instance, and catch-all subscriptions fire on the noise).
/// Nothing reads CreatedUtc/ModifiedUtc on this row.
/// </para>
/// </summary>
public class SubscriptionPollerState : Entity
{
    /// <summary>Fixed singleton id — same pattern SmtpSettings /
    /// BackupSettings use.</summary>
    public static readonly Guid SingletonId =
        new("00000000-0000-0000-0001-000000000003");

    /// <summary>Latest scanned audit-row timestamp. On the first run this
    /// is <see cref="DateTimeOffset.MinValue"/> so the poller back-fills
    /// every existing row; in practice operators create the subscription
    /// before they care about back-fill, so the seed-on-first-poll
    /// behaviour writes a now-stamped cursor without delivering anything
    /// (see <c>SubscriptionPollerJob</c>'s first-run shortcut).</summary>
    public DateTimeOffset LastOccurredUtc { get; set; }
}

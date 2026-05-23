using System.Text;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job (every minute) that drains the
/// <c>email_digest_outbox</c> table — one digest email per subscription
/// whose configured window has elapsed.
///
/// <para>
/// Per-subscription "due" check: the oldest outbox entry for that
/// subscription is older than <c>DigestEveryMinutes</c>. When due, the
/// job collects up to <see cref="MaxEventsPerDigest"/> entries (Octopus
/// parity: 100), builds a single digest email body, sends it via
/// <see cref="EmailImmediateTransport"/>'s send helper, then deletes
/// the included rows from the outbox.
/// </para>
///
/// <para>
/// One <see cref="SubscriptionDelivery"/> row gets written per BATCH
/// (not per event) so the operator-facing history grid shows
/// "Succeeded, 47 events in digest" rather than a wall of identical
/// rows.
/// </para>
/// </summary>
public sealed class EmailDigestFlushJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    EmailDigestSender sender,
    IAuditLog audit,
    ILogger<EmailDigestFlushJob> logger,
    TimeProvider time)
{
    public const string RecurringJobId = "kraken.subscription-digest-flush";

    /// <summary>Octopus parity — caps digest body size at 100 events.</summary>
    internal const int MaxEventsPerDigest = 100;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Two-step "find due subscriptions" — EF Core can't translate the
        // group-by-into-Subscription shape, so we read the candidate
        // subscriptions first then probe each one's outbox for the
        // oldest-AddedUtc. Cheap because the candidate set is small
        // (one row per active digest subscription) and the probe is an
        // indexed lookup.
        List<EventSubscription> candidates;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            candidates = await db.EventSubscriptions
                .Where(s => !s.Disabled
                         && s.Transport == SubscriptionTransport.Email
                         && s.DigestEveryMinutes > 0)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        if (candidates.Count == 0) { return; }

        var due = new List<DueSubscription>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            foreach (var sub in candidates)
            {
                var oldest = await db.EmailDigestOutbox
                    .Where(o => o.SubscriptionId == sub.Id)
                    .OrderBy(o => o.AddedUtc)
                    .Select(o => (DateTimeOffset?)o.AddedUtc)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (oldest is null) { continue; } // outbox empty for this sub
                if (oldest.Value > now.AddMinutes(-sub.DigestEveryMinutes)) { continue; } // window not elapsed

                var count = await db.EmailDigestOutbox
                    .Where(o => o.SubscriptionId == sub.Id)
                    .CountAsync(ct)
                    .ConfigureAwait(false);
                due.Add(new DueSubscription
                {
                    Subscription = sub,
                    OldestAdded  = oldest.Value,
                    Count        = count,
                });
            }
        }

        if (due.Count == 0) { return; }

        foreach (var d in due)
        {
            try
            {
                await FlushOneAsync(d, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Digest flush failed for subscription {SubId}", d.Subscription.Id);
            }
        }
    }

    private async Task FlushOneAsync(DueSubscription due, CancellationToken ct)
    {
        var sub = due.Subscription;
        var started = time.GetUtcNow();
        var startedTimestamp = time.GetTimestamp();

        // Load up to MaxEventsPerDigest entries + their corresponding
        // audit rows so the digest body can render event details.
        List<EmailDigestOutboxEntry> entries;
        List<AuditEntry> events;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            entries = await db.EmailDigestOutbox
                .Where(e => e.SubscriptionId == sub.Id)
                .OrderBy(e => e.AddedUtc)
                .Take(MaxEventsPerDigest)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (entries.Count == 0) { return; }

            var ids = entries.Select(e => e.EventId).ToHashSet();
            events = await db.AuditEntries
                .IgnoreQueryFilters()
                .Where(a => ids.Contains(a.Id))
                .OrderBy(a => a.OccurredUtc)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        // Write the in-flight delivery row first — same visibility contract
        // as immediate delivery (operator hitting the history page during
        // a long SMTP timeout sees something).
        var delivery = new SubscriptionDelivery
        {
            SubscriptionId = sub.Id,
            // Synthetic event id for the batch — use the latest event's
            // id so the operator can pivot from delivery row to its
            // most recent contributing event in /audit. UNIQUE constraint
            // is per (sub, event), so using a real event id here means
            // re-running the flush after a crash will fail-closed on the
            // same (sub, latest-event) pair instead of producing a
            // duplicate delivery row.
            EventId       = events.Count > 0 ? events[^1].Id : Guid.Empty,
            Transport     = SubscriptionTransport.Email,
            StartedUtc    = started,
            Outcome       = SubscriptionDeliveryOutcome.InProgress,
            AttemptNumber = 1,
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.SubscriptionDeliveries.Add(delivery);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.Message.Contains("23505", StringComparison.Ordinal))
            {
                // Crash-recovery path: a previous run already produced a
                // delivery row for this (sub, latest-event). Skip; the
                // next event into the outbox will retry.
                logger.LogDebug(
                    "Digest flush skipped — delivery already exists for sub={SubId}",
                    sub.Id);
                return;
            }
        }

        var body = BuildDigestBody(sub, events);
        var result = await sender.SendAsync(sub, body, ct).ConfigureAwait(false);

        // Finalise the row.
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var tracked = await db.SubscriptionDeliveries
                .FirstAsync(d => d.Id == delivery.Id, ct).ConfigureAwait(false);
            tracked.CompletedUtc = time.GetUtcNow();
            tracked.Duration     = time.GetElapsedTime(startedTimestamp);
            tracked.Outcome      = result.Succeeded
                ? SubscriptionDeliveryOutcome.Succeeded
                : SubscriptionDeliveryOutcome.Failed;
            tracked.Detail       = result.Succeeded
                ? $"{events.Count} event(s) in digest. {result.Detail}"
                : null;
            tracked.ErrorMessage = result.Error;

            // Only delete outbox rows on success — leave them for retry on
            // failure (next flush cycle will pick them up again, fresh
            // attempt counter will go through the delivery row).
            if (result.Succeeded)
            {
                db.EmailDigestOutbox.RemoveRange(entries);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await audit.RecordAsync(
            result.Succeeded
                ? AuditEventType.SubscriptionDeliverySucceeded
                : AuditEventType.SubscriptionDeliveryFailed,
            subjectType: "Subscription",
            subjectId:   sub.Id.ToString(),
            subjectName: sub.Name,
            details:     $"Transport=Email digest, Events={events.Count}, " +
                         (result.Succeeded
                             ? $"Detail={result.Detail}"
                             : $"Error={result.Error}"),
            ct: ct).ConfigureAwait(false);

        logger.LogInformation(
            "Digest flush: sub={SubId} events={Count} outcome={Outcome}",
            sub.Id, events.Count, result.Succeeded ? "ok" : "fail");
    }

    internal static string BuildDigestBody(EventSubscription sub, IReadOnlyList<AuditEntry> events)
    {
        var sb = new StringBuilder();
        sb.Append("KrakenDeploy digest — ").Append(events.Count)
          .Append(" event").Append(events.Count == 1 ? "" : "s").Append('\n');
        sb.Append("Subscription: ").Append(sub.Name).Append('\n');
        sb.Append("Window:       last ").Append(sub.DigestEveryMinutes).Append(" minutes\n\n");

        foreach (var e in events)
        {
            sb.Append("• [").Append(e.OccurredUtc.ToString(
                "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
              .Append("] ").Append(e.EventType);
            if (!string.IsNullOrEmpty(e.SubjectName))
            {
                sb.Append(" — ").Append(e.SubjectName);
            }
            sb.Append('\n');
            if (!string.IsNullOrEmpty(e.Details))
            {
                var detail = e.Details.Length > 200 ? e.Details[..200] + "…" : e.Details;
                sb.Append("    ").Append(detail.Replace("\n", " ")).Append('\n');
            }
        }
        return sb.ToString();
    }

    private sealed class DueSubscription
    {
        public required EventSubscription Subscription { get; init; }
        public DateTimeOffset OldestAdded { get; init; }
        public int Count { get; init; }
    }
}

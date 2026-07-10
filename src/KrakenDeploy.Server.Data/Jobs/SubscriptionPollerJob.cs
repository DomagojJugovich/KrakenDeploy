using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Jobs;

/// <summary>
/// Hangfire recurring job that drives M13.B.2/3 — the Octopus-style
/// outbox poller. Scans audit_entries since the last cursor, matches
/// every active subscription, dispatches each match through
/// <see cref="EventDispatcher"/>.
///
/// <para>
/// Cadence: registered every minute by <c>HangfireJobRegistrar</c>. The
/// poller is idempotent (UNIQUE (subscription, event) on the delivery
/// table) and Hangfire's single-execution lock prevents overlap, so the
/// cadence is just the latency floor — a deployment-failure event is
/// delivered within at most one minute.
/// </para>
///
/// <para>
/// Cursor semantics: <c>LastOccurredUtc</c> in <c>SubscriptionPollerState</c>
/// is the high-water mark. Query reads rows where
/// <c>occurred_utc &gt; cursor</c>. The 30-second look-back window is
/// applied via the matcher's idempotency guard, not a SQL window — see
/// <c>EventDispatcher.IsUniqueViolation</c>.
/// </para>
/// </summary>
public sealed class SubscriptionPollerJob(
    IDbContextFactory<KrakenDbContext> dbFactory,
    EventSubscriptionService subscriptionService,
    EventDispatcher dispatcher,
    MaintenancePause maintenancePause,
    ILogger<SubscriptionPollerJob> logger,
    TimeProvider time)
{
    /// <summary>Hangfire recurring-job id. Removable via the dashboard
    /// when the operator wants to pause every subscription at once
    /// without disabling each row.</summary>
    public const string RecurringJobId = "kraken.subscription-poller";

    /// <summary>
    /// Cap per poll cycle. Prevents a "we've been down for a week"
    /// recovery from generating 100K delivery rows in one tick. The
    /// remainder gets picked up by the next cycle a minute later.
    /// </summary>
    private const int MaxEventsPerCycle = 500;

    /// <summary>
    /// Audit event types the subscription machinery emits as a side effect of
    /// its own delivery work. They MUST be excluded from the scan: the
    /// <see cref="EventDispatcher"/> writes a <c>Subscription.Delivery*</c>
    /// audit row for every delivery, so a catch-all subscription
    /// (system-wide, empty/<c>*</c> pattern) would match those rows, redeliver,
    /// and write yet more — a self-perpetuating storm. It is unbounded because
    /// each new row carries a fresh <c>EventId</c>, so the UNIQUE
    /// (subscription, event) idempotency guard on the delivery table never
    /// trips. Excluding them here is where that loop physically closes; the
    /// rows still land in <c>audit_entries</c> for the /audit UI.
    /// </summary>
    private static readonly string[] SelfGeneratedEventTypes =
    [
        AuditEventType.SubscriptionDeliverySucceeded,
        AuditEventType.SubscriptionDeliveryFailed,
    ];

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (await maintenancePause.ShouldPauseAsync(ct, logger, RecurringJobId)
            .ConfigureAwait(false))
        {
            return;
        }

        // 1. Load the cursor + the active subscription set.
        var (cursor, isFirstRun) = await LoadCursorAsync(ct).ConfigureAwait(false);
        var subscriptions = await subscriptionService.GetAllActiveAsync(ct).ConfigureAwait(false);

        if (subscriptions.Count == 0)
        {
            // Advance the cursor anyway so a future first-subscription
            // create doesn't back-fill the entire audit log. Bumping
            // cursor to "now" matches Octopus's "subscriptions don't see
            // historical events" convention.
            await AdvanceCursorAsync(time.GetUtcNow(), ct).ConfigureAwait(false);
            return;
        }

        // 2. Read new audit rows.
        // Not routed through the audit choke point (AuditExportService): this
        // is the system-context event pump — it must see every Space's rows to
        // fan them out. Space isolation is enforced per subscription at match
        // time (SubscriptionMatcher: a Space-scoped subscription only matches
        // its own Space's events; NULL-Space rows only match system-wide subs).
        List<AuditEntry> events;
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            events = await db.AuditEntries
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(e => e.OccurredUtc > cursor
                            && !SelfGeneratedEventTypes.Contains(e.EventType))
                .OrderBy(e => e.OccurredUtc)
                .ThenBy(e => e.Id)
                .Take(MaxEventsPerCycle)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        if (events.Count == 0)
        {
            return;
        }

        // First-run shortcut: don't back-fill an existing audit log.
        // Just advance the cursor to "latest" so subsequent cycles only
        // see truly new events.
        if (isFirstRun)
        {
            var latest = events[^1].OccurredUtc;
            await AdvanceCursorAsync(latest, ct).ConfigureAwait(false);
            logger.LogInformation(
                "First-run cursor seeded to {Cursor}; {Count} historical events skipped.",
                latest, events.Count);
            return;
        }

        // 3. Dispatch each matched (subscription, event) pair.
        var matched = 0;
        var delivered = 0;
        var failed = 0;
        foreach (var evt in events)
        {
            foreach (var sub in subscriptions)
            {
                if (!SubscriptionMatcher.Matches(sub, evt))
                {
                    continue;
                }
                matched++;
                try
                {
                    var delivery = await dispatcher.DispatchAsync(sub, evt, ct).ConfigureAwait(false);
                    if (delivery is { Outcome: SubscriptionDeliveryOutcome.Succeeded }) { delivered++; }
                    else if (delivery is { Outcome: SubscriptionDeliveryOutcome.Failed }) { failed++; }
                }
                catch (Exception ex)
                {
                    // Dispatcher itself doesn't throw on transport failures
                    // — getting here means something went wrong in our own
                    // bookkeeping. Log + keep going; the next poll cycle
                    // will retry (UNIQUE constraint blocks duplicates).
                    logger.LogError(ex,
                        "SubscriptionPoller: dispatcher threw for sub={SubId} event={EventId}",
                        sub.Id, evt.Id);
                }
            }
        }

        // 4. Advance the cursor to the latest event we scanned. Reads
        //    after a crash mid-dispatch will replay against the UNIQUE
        //    constraint and be no-ops.
        var newCursor = events[^1].OccurredUtc;
        await AdvanceCursorAsync(newCursor, ct).ConfigureAwait(false);

        if (matched > 0)
        {
            logger.LogInformation(
                "SubscriptionPoller cycle: events={Events} matches={Matched} delivered={Delivered} failed={Failed}",
                events.Count, matched, delivered, failed);
        }
    }

    /// <summary>
    /// Loads the cursor + whether this is the first poll. The first-run
    /// boolean controls the "don't back-fill historical events" shortcut.
    /// </summary>
    private async Task<(DateTimeOffset Cursor, bool IsFirstRun)> LoadCursorAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var state = await db.SubscriptionPollerStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SubscriptionPollerState.SingletonId, ct)
            .ConfigureAwait(false);
        return state is null
            ? (DateTimeOffset.MinValue, true)
            : (state.LastOccurredUtc, false);
    }

    private async Task AdvanceCursorAsync(DateTimeOffset value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var state = await db.SubscriptionPollerStates
            .FirstOrDefaultAsync(s => s.Id == SubscriptionPollerState.SingletonId, ct)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new SubscriptionPollerState
            {
                Id              = SubscriptionPollerState.SingletonId,
                LastOccurredUtc = value,
            };
            db.SubscriptionPollerStates.Add(state);
        }
        else
        {
            // Never advance backwards — that would re-deliver every event
            // between the two cursor positions (deduped by UNIQUE, but
            // wasted work; Postgres would also reject the duplicate
            // insert and log noise).
            if (value <= state.LastOccurredUtc) { return; }
            state.LastOccurredUtc = value;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

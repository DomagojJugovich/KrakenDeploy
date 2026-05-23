using System.Net;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Jobs;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// End-to-end integration test for M13.B.2/3 Phase 2: seed an audit
/// row, seed a matching subscription, run the poller, assert the
/// transport got called and the delivery row landed. Uses a capturing
/// HTTP handler so the test never makes a real network call.
/// </summary>
[Collection("Postgres")]
public sealed class SubscriptionPollerJobTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.SubscriptionDeliveries.ExecuteDeleteAsync();
        await db.EventSubscriptions.ExecuteDeleteAsync();
        await db.SubscriptionPollerStates.ExecuteDeleteAsync();
        await db.AuditEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task First_run_seeds_cursor_and_skips_backfill()
    {
        // Seed a historical event + an active subscription.
        await using (var seed = postgres.CreateContext())
        {
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "Deployment.Failed",
                OccurredUtc = DateTimeOffset.UtcNow.AddHours(-1),
                UserDisplay = "t",
                SpaceId     = WellKnown.DefaultSpaceId,
            });
            seed.EventSubscriptions.Add(NewSub("any-event"));
            await seed.SaveChangesAsync();
        }

        var (stub, job) = NewJob();
        await job.ExecuteAsync(default);

        stub.CallCount.Should().Be(0,
            "first poll must NOT back-fill historical events — that " +
            "matches Octopus's 'subscriptions don't see the past' behaviour");

        await using var db = postgres.CreateContext();
        var deliveries = await db.SubscriptionDeliveries.CountAsync();
        deliveries.Should().Be(0);

        // Cursor must be advanced past the seed-default (MinValue) so a
        // second poll (with a new event after the cursor) DOES deliver.
        var state = await db.SubscriptionPollerStates.SingleAsync();
        state.LastOccurredUtc.Should().NotBe(DateTimeOffset.MinValue,
            "the first-run shortcut advances the cursor to the latest " +
            "historical event so the next poll only sees truly new rows");
    }

    [Fact]
    public async Task Second_run_delivers_new_events()
    {
        await using (var seed = postgres.CreateContext())
        {
            seed.EventSubscriptions.Add(NewSub("first"));
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "OldEvent",
                OccurredUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                UserDisplay = "t",
                SpaceId     = WellKnown.DefaultSpaceId,
            });
            await seed.SaveChangesAsync();
        }

        // First run — seeds cursor.
        var (stub, job) = NewJob();
        await job.ExecuteAsync(default);
        stub.CallCount.Should().Be(0);

        // New event after the cursor.
        await using (var seed = postgres.CreateContext())
        {
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "Deployment.Failed",
                OccurredUtc = DateTimeOffset.UtcNow,
                UserDisplay = "t",
                SpaceId     = WellKnown.DefaultSpaceId,
            });
            await seed.SaveChangesAsync();
        }

        // Second run — must deliver.
        await job.ExecuteAsync(default);

        stub.CallCount.Should().Be(1,
            "the new event arrived AFTER cursor seed; second poll cycle " +
            "must deliver it");

        await using var db = postgres.CreateContext();
        var delivery = await db.SubscriptionDeliveries.SingleAsync();
        delivery.Outcome.Should().Be(SubscriptionDeliveryOutcome.Succeeded);
    }

    [Fact]
    public async Task Re_running_does_not_create_duplicate_deliveries()
    {
        // Idempotency contract — the poller can be re-run after a crash
        // mid-cycle, and the UNIQUE (subscription, event) constraint must
        // prevent re-delivery.
        await using (var seed = postgres.CreateContext())
        {
            seed.EventSubscriptions.Add(NewSub("dedup"));
            // Pre-seed a historical event so the first-run shortcut fires
            // and advances the cursor without delivering it.
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "Historical",
                OccurredUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                UserDisplay = "t",
                SpaceId     = WellKnown.DefaultSpaceId,
            });
            await seed.SaveChangesAsync();
        }

        var (stub, job) = NewJob();
        await job.ExecuteAsync(default); // first-run shortcut: advances cursor, no delivery

        // New event lands after the cursor.
        await using (var seed = postgres.CreateContext())
        {
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "Deployment.Failed",
                OccurredUtc = DateTimeOffset.UtcNow,
                UserDisplay = "t",
                SpaceId     = WellKnown.DefaultSpaceId,
            });
            await seed.SaveChangesAsync();
        }

        await job.ExecuteAsync(default); // delivers the new event
        stub.CallCount.Should().Be(1, "first real delivery happened");

        // Pretend we crashed before the cursor advanced — reset it
        // backwards by hand so the next cycle re-reads the same event.
        await using (var db = postgres.CreateContext())
        {
            var state = await db.SubscriptionPollerStates.SingleAsync();
            state.LastOccurredUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await job.ExecuteAsync(default); // re-run — must NOT redeliver

        await using var dbCheck = postgres.CreateContext();
        var deliveries = await dbCheck.SubscriptionDeliveries.CountAsync();
        deliveries.Should().Be(1,
            "UNIQUE (subscription_id, event_id) blocks duplicate row " +
            "creation; the dispatcher catches the violation and returns " +
            "early without invoking the transport a second time");

        stub.CallCount.Should().Be(1,
            "transport should NOT have been called a second time — the " +
            "dispatcher's pre-insert UNIQUE check shortcircuits before " +
            "DeliverAsync");
    }

    [Fact]
    public async Task Disabled_subscription_does_not_deliver()
    {
        await using (var seed = postgres.CreateContext())
        {
            var sub = NewSub("disabled");
            sub.Disabled = true;
            seed.EventSubscriptions.Add(sub);
            await seed.SaveChangesAsync();
        }

        var (stub, job) = NewJob();
        await job.ExecuteAsync(default); // seed cursor

        await using (var seed = postgres.CreateContext())
        {
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "Test",
                OccurredUtc = DateTimeOffset.UtcNow,
                UserDisplay = "t",
                SpaceId     = WellKnown.DefaultSpaceId,
            });
            await seed.SaveChangesAsync();
        }

        await job.ExecuteAsync(default);

        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Foreign_Space_event_does_not_match_Space_scoped_subscription()
    {
        var foreignSpace = Guid.NewGuid();

        await using (var seed = postgres.CreateContext())
        {
            seed.Spaces.Add(new Core.Domain.Spaces.Space
            {
                Id = foreignSpace, Name = "fs", Slug = $"fs-{foreignSpace:N}"
            });
            // Subscription is Space-scoped to Default.
            seed.EventSubscriptions.Add(NewSub("local"));
            await seed.SaveChangesAsync();
        }

        var (stub, job) = NewJob();
        await job.ExecuteAsync(default); // seed cursor

        await using (var seed = postgres.CreateContext())
        {
            seed.AuditEntries.Add(new AuditEntry
            {
                EventType   = "Test",
                OccurredUtc = DateTimeOffset.UtcNow,
                UserDisplay = "t",
                SpaceId     = foreignSpace, // EVENT in foreign Space
            });
            await seed.SaveChangesAsync();
        }

        await job.ExecuteAsync(default);

        stub.CallCount.Should().Be(0,
            "the cross-tenant safety property — a Space-scoped subscription " +
            "MUST NOT receive events from another Space");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static EventSubscription NewSub(string name) => new()
    {
        Name                = name,
        SpaceId             = WellKnown.DefaultSpaceId,
        Transport           = SubscriptionTransport.Webhook,
        TransportConfigJson = """{"url":"https://example.com/hook"}""",
    };

    /// <summary>Builds a poller wired against a capturing HTTP handler so
    /// every webhook call is observable without touching the network.</summary>
    private (CapturingHandler Handler, SubscriptionPollerJob Job) NewJob()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Server:BaseUrl"] = "https://kraken.example.com" }).Build();

        var webhook = new WebhookTransport(
            httpClient, config, NullLogger<WebhookTransport>.Instance, TimeProvider.System);
        var auditLog = new TestAuditLog(postgres);

        var dispatcher = new EventDispatcher(
            postgres,
            new IEventTransport[] { webhook },
            auditLog,
            NullLogger<EventDispatcher>.Instance,
            TimeProvider.System);

        var subscriptionSvc = new EventSubscriptionService(postgres);
        var job = new SubscriptionPollerJob(
            postgres, subscriptionSvc, dispatcher,
            NoopMaintenancePause.For(postgres),
            NullLogger<SubscriptionPollerJob>.Instance, TimeProvider.System);
        return (handler, job);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            });
        }
    }

    /// <summary>Minimal IAuditLog stub that writes rows directly via the
    /// fixture's DbContext. Lets the dispatcher's audit-event emission
    /// path land in the same DB the rest of the test uses without
    /// pulling in the full HTTP context / SpaceContext plumbing.</summary>
    private sealed class TestAuditLog(PostgresFixture pf) : IAuditLog
    {
        public async Task RecordAsync(
            string eventType,
            string? subjectType  = null,
            string? subjectId    = null,
            string? subjectName  = null,
            string? details      = null,
            Guid?   userId       = null,
            string? userDisplay  = null,
            CancellationToken ct = default)
        {
            await using var db = pf.CreateContext();
            db.AuditEntries.Add(new AuditEntry
            {
                EventType   = eventType,
                OccurredUtc = DateTimeOffset.UtcNow,
                UserDisplay = userDisplay ?? "test",
                SpaceId     = null, // system-level
                SubjectType = subjectType,
                SubjectId   = subjectId,
                SubjectName = subjectName,
                Details     = details,
            });
            await db.SaveChangesAsync(ct);
        }
    }
}

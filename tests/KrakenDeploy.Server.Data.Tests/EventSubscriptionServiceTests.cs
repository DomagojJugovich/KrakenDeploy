using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.B.2/3 Phase 1 — <see cref="EventSubscriptionService"/>.
/// Pins the validation contract (transport-config schema-on-save) and the
/// Space + system-wide visibility rules the UI relies on.
/// </summary>
[Collection("Postgres")]
public sealed class EventSubscriptionServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.EventSubscriptions.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── CRUD basics ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_persists_and_returns_row()
    {
        var svc = new EventSubscriptionService(postgres);

        var created = await svc.CreateAsync(SampleWebhookSub());

        created.Id.Should().NotBe(Guid.Empty);
        (await svc.GetAsync(created.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_does_not_change_SpaceId()
    {
        // Moving a row between Space-scoped and system-wide changes the
        // security tier (sys-admin only); the service rejects the change
        // by ignoring the new SpaceId on update.
        var svc = new EventSubscriptionService(postgres);
        var created = await svc.CreateAsync(SampleWebhookSub());

        var edit = SampleWebhookSub();
        edit.SpaceId = null; // try to promote to system-wide via update
        await svc.UpdateAsync(created.Id, edit);

        var reload = await svc.GetAsync(created.Id);
        reload!.SpaceId.Should().Be(WellKnown.DefaultSpaceId,
            "SpaceId must stay fixed on update — promotion to system-wide " +
            "is a delete-and-recreate operation");
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_missing_row()
    {
        var svc = new EventSubscriptionService(postgres);
        (await svc.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    // ── Visibility ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetForSpaceAsync_returns_Space_scoped_plus_system_wide()
    {
        var svc = new EventSubscriptionService(postgres);
        var otherSpace = Guid.NewGuid();

        // Three rows: this Space, foreign Space, system-wide.
        await using (var seed = postgres.CreateContext())
        {
            seed.Spaces.Add(new Core.Domain.Spaces.Space
            {
                Id = otherSpace, Name = "other", Slug = $"o-{otherSpace:N}"
            });
            seed.EventSubscriptions.AddRange(
                new EventSubscription { Name = "in-space",     SpaceId = WellKnown.DefaultSpaceId, TransportConfigJson = WebhookJson },
                new EventSubscription { Name = "other-space",  SpaceId = otherSpace,               TransportConfigJson = WebhookJson },
                new EventSubscription { Name = "system-wide",  SpaceId = null,                     TransportConfigJson = WebhookJson });
            await seed.SaveChangesAsync();
        }

        var visible = await svc.GetForSpaceAsync(WellKnown.DefaultSpaceId);

        visible.Should().HaveCount(2,
            "the foreign-Space subscription must NOT appear in this Space's list");
        visible.Select(s => s.Name).Should().BeEquivalentTo(["in-space", "system-wide"]);
        visible[0].SpaceId.Should().BeNull(
            "system-wide rows sort to the top so operators see what's " +
            "firing globally before their own subscriptions");
    }

    [Fact]
    public async Task GetAllActiveAsync_excludes_disabled_rows()
    {
        var svc = new EventSubscriptionService(postgres);
        await using (var seed = postgres.CreateContext())
        {
            seed.EventSubscriptions.AddRange(
                new EventSubscription { Name = "live",     SpaceId = WellKnown.DefaultSpaceId, TransportConfigJson = WebhookJson, Disabled = false },
                new EventSubscription { Name = "paused",   SpaceId = WellKnown.DefaultSpaceId, TransportConfigJson = WebhookJson, Disabled = true });
            await seed.SaveChangesAsync();
        }

        var active = await svc.GetAllActiveAsync();

        active.Should().HaveCount(1);
        active[0].Name.Should().Be("live",
            "disabled rows are skipped by the poller path; this is what " +
            "lets operators 'temporarily silence' a noisy subscription");
    }

    // ── Validation: transport-config schema-on-save ───────────────────────

    [Fact]
    public async Task Empty_name_throws()
    {
        var svc = new EventSubscriptionService(postgres);
        var input = SampleWebhookSub();
        input.Name = "";

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*name is required*");
    }

    [Fact]
    public async Task Webhook_transport_rejects_missing_url()
    {
        var svc = new EventSubscriptionService(postgres);
        var input = SampleWebhookSub();
        input.TransportConfigJson = "{}";

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*'url'*");
    }

    [Fact]
    public async Task Email_transport_rejects_missing_recipients()
    {
        var svc = new EventSubscriptionService(postgres);
        var input = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Email,
            TransportConfigJson = """{"recipients":[]}""",
        };

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*'recipients'*");
    }

    [Fact]
    public async Task Runbook_transport_rejects_missing_runbookId()
    {
        var svc = new EventSubscriptionService(postgres);
        var input = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Runbook,
            TransportConfigJson = "{}",
        };

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*'runbookId'*");
    }

    [Fact]
    public async Task AiInspect_transport_accepts_empty_config()
    {
        // AI transport falls back to a built-in prompt template when no
        // prompt is supplied — the validator must accept an empty object.
        var svc = new EventSubscriptionService(postgres);
        var input = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.AiInspect,
            TransportConfigJson = "{}",
        };

        var act = async () => await svc.CreateAsync(input);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Malformed_JSON_throws()
    {
        var svc = new EventSubscriptionService(postgres);
        var input = SampleWebhookSub();
        input.TransportConfigJson = "{not json";

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not valid JSON*");
    }

    [Fact]
    public async Task DigestEveryMinutes_rejected_for_non_Email_transport()
    {
        // Per-transport invariant: DigestEveryMinutes is meaningful only
        // for Email; setting it on Webhook is a bug-shaped configuration
        // that the validator must catch at save time, not silently
        // ignore at deliver time.
        var svc = new EventSubscriptionService(postgres);
        var input = SampleWebhookSub();
        input.DigestEveryMinutes = 15;

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DigestEveryMinutes is only meaningful for the Email transport*");
    }

    [Fact]
    public async Task Negative_DigestEveryMinutes_rejected()
    {
        var svc = new EventSubscriptionService(postgres);
        var input = new EventSubscription
        {
            Name                = "test",
            SpaceId             = WellKnown.DefaultSpaceId,
            Transport           = SubscriptionTransport.Email,
            TransportConfigJson = """{"recipients":["ops@example.com"]}""",
            DigestEveryMinutes  = -1,
        };

        var act = async () => await svc.CreateAsync(input);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DigestEveryMinutes*");
    }

    // ── M11.C seeder (Phase 5) ────────────────────────────────────────────

    [Fact]
    public async Task EnsureDiagnoseDeploymentFailedAsync_creates_built_in_subscription_when_missing()
    {
        var svc = new EventSubscriptionService(postgres);

        var seeded = await svc.EnsureDiagnoseDeploymentFailedAsync(WellKnown.DefaultSpaceId);

        seeded.SpaceId.Should().Be(WellKnown.DefaultSpaceId);
        seeded.Transport.Should().Be(SubscriptionTransport.AiInspect);
        seeded.EventTypePatterns.Should().ContainSingle("Deployment.Failed");
        seeded.Disabled.Should().BeFalse();
        seeded.Name.Should().Contain("Built-in",
            "the name flags it as system-seeded so operators don't confuse it " +
            "with their own subscriptions");
    }

    [Fact]
    public async Task EnsureDiagnoseDeploymentFailedAsync_is_idempotent()
    {
        // Operator flips Diagnosis on / off / on — the seeder must not
        // produce duplicate rows.
        var svc = new EventSubscriptionService(postgres);

        var first  = await svc.EnsureDiagnoseDeploymentFailedAsync(WellKnown.DefaultSpaceId);
        var second = await svc.EnsureDiagnoseDeploymentFailedAsync(WellKnown.DefaultSpaceId);

        first.Id.Should().Be(second.Id,
            "second call returned the existing row, not a fresh one");

        await using var db = postgres.CreateContext();
        var count = await db.EventSubscriptions
            .Where(s => s.SpaceId == WellKnown.DefaultSpaceId && s.Name.Contains("Built-in"))
            .CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task EnsureDiagnoseDeploymentFailedAsync_does_not_overwrite_operator_changes()
    {
        // Pin the "operator can edit the seeded row without it being
        // reset on next enable" contract.
        var svc = new EventSubscriptionService(postgres);

        var seeded = await svc.EnsureDiagnoseDeploymentFailedAsync(WellKnown.DefaultSpaceId);

        // Operator edits the row.
        var edit = new EventSubscription
        {
            SpaceId             = seeded.SpaceId,
            Name                = seeded.Name,
            Description         = "Operator-customised",
            EventTypePatterns   = ["Deployment.*"], // broader scope
            Transport           = SubscriptionTransport.AiInspect,
            TransportConfigJson = """{"prompt":"Custom prompt"}""",
            Disabled            = true,
        };
        await svc.UpdateAsync(seeded.Id, edit);

        // Operator flips Diagnosis off then on — seeder runs again.
        await svc.EnsureDiagnoseDeploymentFailedAsync(WellKnown.DefaultSpaceId);

        var reloaded = (await svc.GetAsync(seeded.Id))!;
        reloaded.Description.Should().Be("Operator-customised");
        reloaded.EventTypePatterns.Should().Equal("Deployment.*");
        reloaded.Disabled.Should().BeTrue(
            "the seeder finds-or-creates; it doesn't overwrite an existing " +
            "operator-customised row");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private const string WebhookJson =
        """{"url":"https://example.com/hook","secret":"shh"}""";

    private static EventSubscription SampleWebhookSub() => new()
    {
        Name                = "test",
        SpaceId             = WellKnown.DefaultSpaceId,
        Transport           = SubscriptionTransport.Webhook,
        TransportConfigJson = WebhookJson,
    };
}

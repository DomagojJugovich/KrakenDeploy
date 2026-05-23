using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Subscriptions;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Pure-function unit tests for <see cref="SubscriptionMatcher"/>. This is
/// the hot path the poller calls once per (subscription × event), so the
/// matrix of "what matches what" needs to stay airtight.
/// </summary>
public sealed class SubscriptionMatcherTests
{
    // ── Event-type pattern matching ───────────────────────────────────────

    [Theory]
    [InlineData("Deployment.Failed",  "Deployment.Failed",   true)]
    [InlineData("Deployment.Failed",  "deployment.failed",   true)]   // case-insensitive
    [InlineData("Deployment.Failed",  "Deployment.Succeeded", false)]
    [InlineData("Deployment.*",       "Deployment.Failed",   true)]
    [InlineData("Deployment.*",       "Deployment.Succeeded", true)]
    [InlineData("Deployment.*",       "Release.Created",     false)]
    [InlineData("*",                  "Deployment.Failed",   true)]
    [InlineData("*",                  "anything-at-all",     true)]
    public void EventTypeMatches_pattern_to_event_type(
        string pattern, string eventType, bool expected)
    {
        SubscriptionMatcher.EventTypeMatches(pattern, eventType).Should().Be(expected);
    }

    // ── Top-level Matches: empty filters = "any" ──────────────────────────

    [Fact]
    public void Subscription_with_empty_filters_matches_any_event_in_its_Space()
    {
        var sub = SubInDefaultSpace();
        var evt = EventOf("Deployment.Failed", spaceId: WellKnown.DefaultSpaceId);

        SubscriptionMatcher.Matches(sub, evt).Should().BeTrue();
    }

    [Fact]
    public void Disabled_subscription_matches_nothing()
    {
        var sub = SubInDefaultSpace();
        sub.Disabled = true;
        var evt = EventOf("Anything.Happened");

        SubscriptionMatcher.Matches(sub, evt).Should().BeFalse();
    }

    // ── Space-scope ────────────────────────────────────────────────────────

    [Fact]
    public void Space_scoped_sub_does_not_match_event_from_foreign_Space()
    {
        var sub = SubInDefaultSpace();
        var evt = EventOf("Deployment.Failed", spaceId: Guid.NewGuid());

        SubscriptionMatcher.Matches(sub, evt).Should().BeFalse(
            "a subscription Space-scoped to A must NOT see events from " +
            "Space B — that's a real cross-tenant leak risk if it does");
    }

    [Fact]
    public void System_wide_sub_matches_events_from_every_Space()
    {
        var sub = new EventSubscription { Name = "any", SpaceId = null };

        SubscriptionMatcher.Matches(sub, EventOf("A", spaceId: Guid.NewGuid())).Should().BeTrue();
        SubscriptionMatcher.Matches(sub, EventOf("B", spaceId: Guid.NewGuid())).Should().BeTrue();
        SubscriptionMatcher.Matches(sub, SystemLevelEvent("C")).Should().BeTrue(
            "system-level audit rows (License.Uploaded etc.) carry " +
            "SpaceId=null; they only match system-wide subscriptions");
    }

    [Fact]
    public void Space_scoped_sub_does_not_match_system_level_event()
    {
        var sub = SubInDefaultSpace();

        SubscriptionMatcher.Matches(sub, SystemLevelEvent("License.Uploaded"))
            .Should().BeFalse(
                "system-level events shouldn't reach Space-scoped subscriptions");
    }

    // ── Event-type filter ─────────────────────────────────────────────────

    [Fact]
    public void Event_type_filter_uses_OR_within_dimension()
    {
        var sub = SubInDefaultSpace();
        sub.EventTypePatterns = ["Deployment.Failed", "Backup.Failed"];

        SubscriptionMatcher.Matches(sub, EventOf("Deployment.Failed")).Should().BeTrue();
        SubscriptionMatcher.Matches(sub, EventOf("Backup.Failed")).Should().BeTrue();
        SubscriptionMatcher.Matches(sub, EventOf("Deployment.Succeeded")).Should().BeFalse();
    }

    [Fact]
    public void Wildcard_and_exact_patterns_can_coexist()
    {
        var sub = SubInDefaultSpace();
        sub.EventTypePatterns = ["Deployment.*", "Backup.Failed"];

        SubscriptionMatcher.Matches(sub, EventOf("Deployment.Started")).Should().BeTrue();
        SubscriptionMatcher.Matches(sub, EventOf("Backup.Failed")).Should().BeTrue();
        SubscriptionMatcher.Matches(sub, EventOf("Backup.Completed")).Should().BeFalse(
            "Backup.Completed is not in either pattern");
    }

    // ── Project filter (lazy resolution) ──────────────────────────────────

    [Fact]
    public void Project_filter_calls_resolver_only_when_filter_is_non_empty()
    {
        var sub = SubInDefaultSpace();
        // No project filter set.
        var resolverCalled = false;
        Func<AuditEntry, Guid?> resolver = _ => { resolverCalled = true; return null; };

        SubscriptionMatcher.Matches(sub, EventOf("Anything"), resolveProjectId: resolver)
            .Should().BeTrue();
        resolverCalled.Should().BeFalse(
            "empty project filter must skip the resolver — the lazy path " +
            "is the common one, and the resolver may be expensive");
    }

    [Fact]
    public void Project_filter_returns_false_when_resolver_returns_null()
    {
        var sub = SubInDefaultSpace();
        sub.ProjectIds = [Guid.NewGuid()];
        Func<AuditEntry, Guid?> resolver = _ => null;

        SubscriptionMatcher.Matches(sub, EventOf("Anything"), resolveProjectId: resolver)
            .Should().BeFalse(
                "filter says 'event must be for project X' but resolver " +
                "can't determine a project — fail closed");
    }

    [Fact]
    public void Project_filter_returns_false_when_resolver_returns_unlisted_project()
    {
        var sub = SubInDefaultSpace();
        var listedProject = Guid.NewGuid();
        sub.ProjectIds = [listedProject];
        Func<AuditEntry, Guid?> resolver = _ => Guid.NewGuid(); // not listed

        SubscriptionMatcher.Matches(sub, EventOf("Anything"), resolveProjectId: resolver)
            .Should().BeFalse();
    }

    [Fact]
    public void Project_filter_returns_true_when_resolver_returns_listed_project()
    {
        var sub = SubInDefaultSpace();
        var listedProject = Guid.NewGuid();
        sub.ProjectIds = [listedProject, Guid.NewGuid()];
        Func<AuditEntry, Guid?> resolver = _ => listedProject;

        SubscriptionMatcher.Matches(sub, EventOf("Anything"), resolveProjectId: resolver)
            .Should().BeTrue();
    }

    // ── Combined filter — AND across dimensions ───────────────────────────

    [Fact]
    public void All_filters_must_pass_simultaneously()
    {
        var sub = SubInDefaultSpace();
        var project = Guid.NewGuid();
        sub.EventTypePatterns = ["Deployment.Failed"];
        sub.ProjectIds        = [project];

        Func<AuditEntry, Guid?> matchingResolver  = _ => project;
        Func<AuditEntry, Guid?> mismatchedResolver = _ => Guid.NewGuid();

        // Both match → true.
        SubscriptionMatcher.Matches(
            sub, EventOf("Deployment.Failed"),
            resolveProjectId: matchingResolver).Should().BeTrue();

        // Event-type matches but project doesn't → false.
        SubscriptionMatcher.Matches(
            sub, EventOf("Deployment.Failed"),
            resolveProjectId: mismatchedResolver).Should().BeFalse();

        // Project matches but event-type doesn't → false.
        SubscriptionMatcher.Matches(
            sub, EventOf("Deployment.Succeeded"),
            resolveProjectId: matchingResolver).Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static EventSubscription SubInDefaultSpace() => new()
    {
        Name    = "test",
        SpaceId = WellKnown.DefaultSpaceId,
    };

    /// <summary>Default helper: Space-scoped event. <paramref name="spaceId"/>
    /// null means "use DefaultSpaceId" — for tests that want an actual
    /// null Space (system-level event), use <see cref="SystemLevelEvent"/>.</summary>
    private static AuditEntry EventOf(string eventType, Guid? spaceId = null) => new()
    {
        EventType   = eventType,
        OccurredUtc = DateTimeOffset.UtcNow,
        UserDisplay = "test",
        SpaceId     = spaceId ?? WellKnown.DefaultSpaceId,
    };

    /// <summary>System-level event (SpaceId=null on the row) — License.Uploaded,
    /// User.SignedIn against the cross-Space audit etc.</summary>
    private static AuditEntry SystemLevelEvent(string eventType) => new()
    {
        EventType   = eventType,
        OccurredUtc = DateTimeOffset.UtcNow,
        UserDisplay = "test",
        SpaceId     = null,
    };
}

using FluentAssertions;
using KrakenDeploy.Router;

namespace KrakenDeploy.Router.Tests;

/// <summary>
/// The routing decision table (docs/blue-green-slot-deployment.md §3/§5/§6):
/// live pins are honored — Deploying (pre-health-gate) only via the explicit
/// header, never a cookie — dead or missing pins fall back to the default and
/// re-issue the cookie, and a missing default is a loud 503 rather than a guess.
/// </summary>
public class SlotRouteDeciderTests
{
    private static RouterSnapshot Snapshot(
        string? defaultId, params RouterReleaseEntry[] live) =>
        new(defaultId, live.ToDictionary(e => e.ReleaseId, StringComparer.Ordinal));

    private static PinExtraction Cookie(string? value) => new(value, FromHeader: false);

    private static PinExtraction Header(string value) => new(value, FromHeader: true);

    private static readonly RouterReleaseEntry Active = new("rel-b", 2, Status: 1);
    private static readonly RouterReleaseEntry Draining = new("rel-a", 1, Status: 2);
    private static readonly RouterReleaseEntry Deploying = new("rel-c", 3, Status: 0);

    [Fact]
    public void No_pin_routes_to_the_default_and_issues_the_cookie()
    {
        var decision = SlotRouteDecider.Decide(Snapshot("rel-b", Active, Draining), Cookie(null));

        decision.Should().Be(new RouteDecision("rel-b", 2, IssuePin: true));
    }

    [Fact]
    public void A_live_draining_pin_stays_on_its_slot_without_reissuing()
    {
        var decision = SlotRouteDecider.Decide(Snapshot("rel-b", Active, Draining), Cookie("rel-a"));

        decision.Should().Be(new RouteDecision("rel-a", 1, IssuePin: false));
    }

    [Fact]
    public void A_deploying_pin_via_the_header_reaches_its_slot_for_the_health_gate()
    {
        var decision = SlotRouteDecider.Decide(Snapshot("rel-b", Active, Deploying), Header("rel-c"));

        decision.Should().Be(new RouteDecision("rel-c", 3, IssuePin: false));
    }

    [Fact]
    public void A_deploying_pin_via_a_cookie_is_ignored_and_falls_back_to_the_default()
    {
        // A browser cookie must never reach a build that has not passed its
        // health gate — only the explicit operator header may.
        var decision = SlotRouteDecider.Decide(Snapshot("rel-b", Active, Deploying), Cookie("rel-c"));

        decision.Should().Be(new RouteDecision("rel-b", 2, IssuePin: true));
    }

    [Fact]
    public void A_retired_or_unknown_pin_falls_back_to_the_default_and_repins()
    {
        var decision = SlotRouteDecider.Decide(Snapshot("rel-b", Active), Cookie("rel-gone"));

        decision.Should().Be(new RouteDecision("rel-b", 2, IssuePin: true));
    }

    [Fact]
    public void A_pin_equal_to_the_default_does_not_reissue()
    {
        var decision = SlotRouteDecider.Decide(Snapshot("rel-b", Active), Cookie("rel-b"));

        decision.Should().Be(new RouteDecision("rel-b", 2, IssuePin: false));
    }

    [Fact]
    public void No_default_is_unroutable()
        => SlotRouteDecider.Decide(Snapshot(null, Draining), Cookie(null)).Should().BeNull();

    [Fact]
    public void A_default_pointing_at_a_dead_release_is_unroutable()
        => SlotRouteDecider.Decide(Snapshot("rel-gone", Active), Cookie(null)).Should().BeNull();
}

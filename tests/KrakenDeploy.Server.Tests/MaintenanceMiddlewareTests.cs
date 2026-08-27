using FluentAssertions;
using KrakenDeploy.Server.Maintenance;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M13.A.3 middleware's path-routing contract. The
/// full HTTP pipeline (auth → middleware → page) is integration territory
/// that needs a WebApplicationFactory harness; this file pins the
/// path-exemption table, which is the bit most likely to drift if a
/// future contributor adds a new endpoint and forgets to exempt it.
/// </summary>
public sealed class MaintenanceMiddlewareTests
{
    [Theory]
    // ── Exempt paths (must NOT be blocked even when maintenance is on) ──
    [InlineData("/login",                              true)]
    [InlineData("/login/external",                     true, "OIDC sub-routes")]
    [InlineData("/logout",                             true)]
    [InlineData("/_blazor",                            true)]
    [InlineData("/_blazor?id=abc",                     true, "SignalR connection negotiation")]
    [InlineData("/_framework/blazor.web.js",           true)]
    [InlineData("/_content/Radzen.Blazor/index.css",   true)]
    [InlineData("/api/agents/register",                true, "agent bootstrap must stay alive")]
    [InlineData("/api/agents/foo/bar",                 true)]
    [InlineData("/hubs/agent",                         true, "SignalR agent transport must stay alive")]
    [InlineData("/hubs/agent/negotiate",               true, "SignalR negotiate is a POST")]
    [InlineData("/healthz",                            true, "monitoring must not flip red")]
    [InlineData("/api/diagnostics/report.zip",         true, "ops still needs diagnostics during maintenance")]
    [InlineData("/hangfire",                           true)]
    [InlineData("/hangfire/jobs/enqueued",             true)]
    [InlineData("/configuration/settings",             true, "the unified settings page hosts the maintenance toggle")]
    [InlineData("/api/maintenance",                    true, "API counterpart")]
    // ── Space-prefixed forms (/s/{slug}/…) must still be exempt: the maintenance
    //    toggle now lives on the unified page at /s/{slug}/configuration/settings ──
    [InlineData("/s/default/configuration/settings",   true, "space-prefixed settings page")]
    [InlineData("/s/acme/configuration/settings",      true, "space-prefixed for a named account")]
    // ── Non-exempt paths (will be blocked for non-bypass callers) ──
    [InlineData("/api/projects",                       false)]
    // BG1/T13: the REST creation routes are NOT exempt — a non-bypass caller is
    // 503'd here; a BypassMaintenance caller passes the middleware and is then
    // refused by the unconditional service-layer gate (see
    // MaintenanceCreationRefusalTests in Server.Data.Tests).
    [InlineData("/api/deployments",                    false, "REST deployment creation stays gated")]
    [InlineData("/api/runbooks/abc/runs",              false, "REST runbook trigger stays gated")]
    [InlineData("/mcp",                                false, "MCP mutations (incl. ad-hoc dispatch) stay middleware-gated — T13")]
    [InlineData("/api/audit/export.csv",               false, "audit export is GET so the middleware skips on method anyway, but path-wise non-exempt")]
    [InlineData("/configuration/users",                false)]
    [InlineData("/s/acme/configuration/users",         false, "space-prefixed non-exempt stays blocked")]
    [InlineData("/projects/foo",                       false)]
    [InlineData("/",                                   false, "root not exempt — only specific paths bypass")]
    [InlineData("/s/acme",                             false, "space root with no sub-path is not exempt")]
    public void IsExemptPath_classifies_known_paths(
        string path, bool expectedExempt, string? _ = null)
    {
        MaintenanceMiddleware.IsExemptPath(path).Should().Be(expectedExempt);
    }

    [Fact]
    public void IsExemptPath_match_is_case_insensitive()
    {
        // Operators occasionally hit endpoints with mixed casing (browser
        // URLs, copy-paste from docs). Match by prefix must not be
        // case-sensitive — otherwise a /Login (with capital L) would
        // hit the 503 wall.
        MaintenanceMiddleware.IsExemptPath("/Login").Should().BeTrue();
        MaintenanceMiddleware.IsExemptPath("/HEALTHZ").Should().BeTrue();
        MaintenanceMiddleware.IsExemptPath("/Hangfire/Servers").Should().BeTrue();
    }

    [Fact]
    public void IsExemptPath_match_is_prefix_based()
    {
        // The contract is "prefix match" so /api/agents matches every
        // sub-route. Confirm a partial-match-but-not-prefix doesn't
        // accidentally exempt unrelated paths (e.g. /api/agentsblah
        // should also match because StartsWith, but /healthZilla should
        // also be considered exempt — that's the StartsWith trade-off).
        MaintenanceMiddleware.IsExemptPath("/api/agents-stuff").Should().BeTrue(
            "StartsWith semantics; if this is a problem the prefix " +
            "list should change to require trailing slash. For now, no " +
            "known endpoints collide, so the simple prefix match wins.");
        MaintenanceMiddleware.IsExemptPath("/api/projects").Should().BeFalse(
            "non-exempt prefix stays non-exempt");
    }

    [Fact]
    public void Empty_path_is_not_exempt()
    {
        // Defensive — empty PathBase shouldn't accidentally count as
        // matching any prefix. Empty.StartsWith("/login") is false, so
        // the natural behaviour is correct; pin it.
        MaintenanceMiddleware.IsExemptPath("").Should().BeFalse();
        MaintenanceMiddleware.IsExemptPath("/").Should().BeFalse();
    }
}

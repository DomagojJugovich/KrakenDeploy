using FluentAssertions;
using KrakenDeploy.Server.Spaces;
using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Tests for the Space-in-URL path helpers: splitting <c>/s/{slug}/rest</c>,
/// building prefixed paths, and the space-agnostic skip-list.
/// </summary>
public sealed class SpaceRoutingTests
{
    [Theory]
    [InlineData("/s/space2/projects", "space2", "/projects")]
    [InlineData("/s/space2/projects/foo/releases", "space2", "/projects/foo/releases")]
    [InlineData("/s/default", "default", "/")]
    [InlineData("/s/default/", "default", "/")]
    [InlineData("/projects", null, "/projects")]
    [InlineData("/", null, "/")]
    [InlineData("/s/", null, "/s/")]            // malformed: empty slug
    [InlineData("", null, "/")]
    public void Split_extracts_slug_and_rest(string path, string? slug, string rest)
    {
        var (s, r) = SpaceRouting.Split(path);
        s.Should().Be(slug);
        r.Should().Be(rest);
    }

    [Theory]
    [InlineData("space2", "/projects", "/s/space2/projects")]
    [InlineData("space2", "/", "/s/space2")]
    [InlineData("space2", "", "/s/space2")]
    [InlineData("default", "/projects/foo/releases", "/s/default/projects/foo/releases")]
    [InlineData("default", "relative", "/s/default/relative")]
    public void BuildPath_prefixes_slug(string slug, string rel, string expected)
        => SpaceRouting.BuildPath(slug, rel).Should().Be(expected);

    [Fact]
    public void Split_then_Build_round_trips()
    {
        var (slug, rest) = SpaceRouting.Split("/s/space2/projects/foo");
        SpaceRouting.BuildPath(slug!, rest).Should().Be("/s/space2/projects/foo");
    }

    [Theory]
    [InlineData("/api/spaces", true)]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/_framework/blazor.web.js", true)]
    [InlineData("/_content/Radzen.Blazor/Radzen.Blazor.js", true)]
    [InlineData("/hubs/agent", true)]
    [InlineData("/hangfire", true)]
    [InlineData("/healthz", true)]
    [InlineData("/login", true)]
    [InlineData("/logout", true)]
    [InlineData("/Error", true)]
    [InlineData("/signin-oidc_abc123", true)]
    [InlineData("/app.css", true)]                 // static asset (has extension)
    [InlineData("/favicon.ico", true)]
    [InlineData("/projects", false)]               // page route
    [InlineData("/", false)]
    [InlineData("/configuration/spaces", false)]
    [InlineData("/projects/my-app/releases", false)]
    public void IsSpaceAgnostic_classifies_paths(string path, bool agnostic)
        => SpaceRouting.IsSpaceAgnostic(new PathString(path)).Should().Be(agnostic);
}

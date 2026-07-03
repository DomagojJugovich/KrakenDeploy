using FluentAssertions;
using KrakenDeploy.Router;
using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Router.Tests;

/// <summary>
/// Pin extraction precedence (explicit header > secure cookie > dev cookie) and
/// cookie issuance semantics (§3): <c>__Host-kd_ver</c> + Secure over HTTPS
/// (direct or via X-Forwarded-Proto), plain <c>kd_ver</c> over dev HTTP.
/// </summary>
public class VersionPinTests
{
    private static DefaultHttpContext Context() => new();

    [Fact]
    public void Extract_prefers_the_explicit_header_over_cookies()
    {
        var ctx = Context();
        ctx.Request.Headers["X-KD-Release"] = "rel-header";
        ctx.Request.Headers.Cookie = "__Host-kd_ver=rel-cookie";

        VersionPin.Extract(ctx.Request).Should().Be(new PinExtraction("rel-header", FromHeader: true));
    }

    [Fact]
    public void Extract_reads_the_host_prefixed_cookie_then_the_dev_cookie()
    {
        var ctx = Context();
        ctx.Request.Headers.Cookie = "__Host-kd_ver=rel-secure; kd_ver=rel-dev";
        VersionPin.Extract(ctx.Request).Should().Be(new PinExtraction("rel-secure", FromHeader: false));

        var devCtx = Context();
        devCtx.Request.Headers.Cookie = "kd_ver=rel-dev";
        VersionPin.Extract(devCtx.Request).Should().Be(new PinExtraction("rel-dev", FromHeader: false));
    }

    [Fact]
    public void Extract_returns_null_when_nothing_is_pinned()
        => VersionPin.Extract(Context().Request).Value.Should().BeNull();

    [Fact]
    public void Issue_over_https_uses_the_host_prefix_and_secure()
    {
        var ctx = Context();
        ctx.Request.IsHttps = true;

        VersionPin.Issue(ctx, "rel-x");

        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        setCookie.Should().StartWith("__Host-kd_ver=rel-x");
        setCookie.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=lax");
        setCookie.Should().Contain("path=/");
    }

    [Fact]
    public void Issue_behind_a_tls_terminating_edge_is_still_secure()
    {
        var ctx = Context();
        ctx.Request.Headers["X-Forwarded-Proto"] = "https";

        VersionPin.Issue(ctx, "rel-x");

        ctx.Response.Headers.SetCookie.ToString().Should().StartWith("__Host-kd_ver=rel-x");
    }

    [Fact]
    public void Issue_over_plain_http_degrades_to_the_dev_cookie_name()
    {
        var ctx = Context();

        VersionPin.Issue(ctx, "rel-x");

        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        setCookie.Should().StartWith("kd_ver=rel-x");
        setCookie.Should().NotContain("secure");
    }
}

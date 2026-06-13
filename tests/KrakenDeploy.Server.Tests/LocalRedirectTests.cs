using FluentAssertions;
using KrakenDeploy.Server.Web;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Regression tests for the open-redirect fix shared by the login return-url,
/// the OIDC challenge return-url, and the Space-switch return-url. The previous
/// checks (Uri.IsWellFormedUriString(.., Relative) and StartsWith('/')) both
/// accepted the protocol-relative "//evil.com", redirecting victims off-site.
/// </summary>
public sealed class LocalRedirectTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/projects?id=1&tab=a", "/projects?id=1&tab=a")]
    [InlineData("/a//b/c", "/a//b/c")]              // double-slash later in the path is a valid local path
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("//evil.com", "/")]                 // protocol-relative -> off-site
    [InlineData("//evil.com/path", "/")]
    [InlineData("/\\evil.com", "/")]                // backslash protocol-relative
    [InlineData("https://evil.com", "/")]           // absolute
    [InlineData("http://evil.com/x", "/")]
    [InlineData("evil.com", "/")]                   // no leading slash
    [InlineData("javascript:alert(1)", "/")]        // scheme
    public void MakeSafe_returns_only_safe_local_paths(string? input, string expected)
        => LocalRedirect.MakeSafe(input).Should().Be(expected);
}

using System.Net;
using FluentAssertions;
using KrakenDeploy.Server.Data.Net;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests for the outbound-URL SSRF guard (CAT 5). It blocks ONLY loopback,
/// link-local/metadata, and the unspecified address — internal RFC1918 hosts
/// stay reachable so on-prem webhook receivers keep working. No Docker / DB.
/// </summary>
public sealed class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]            // loopback
    [InlineData("127.9.9.9", true)]            // 127.0.0.0/8 entirely
    [InlineData("::1", true)]                  // IPv6 loopback
    [InlineData("169.254.0.1", true)]          // link-local
    [InlineData("169.254.169.254", true)]      // cloud metadata
    [InlineData("fe80::1", true)]              // IPv6 link-local
    [InlineData("0.0.0.0", true)]              // unspecified
    [InlineData("::", true)]                   // IPv6 unspecified
    [InlineData("::ffff:127.0.0.1", true)]     // IPv4-mapped loopback
    [InlineData("10.0.0.5", false)]            // RFC1918 — allowed (on-prem)
    [InlineData("172.16.4.4", false)]          // RFC1918 — allowed
    [InlineData("192.168.1.10", false)]        // RFC1918 — allowed
    [InlineData("8.8.8.8", false)]             // public
    [InlineData("2001:4860:4860::8888", false)] // public IPv6
    public void IsBlocked_classifies_addresses(string ip, bool blocked)
        => SsrfGuard.IsBlocked(IPAddress.Parse(ip)).Should().Be(blocked);

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("http://0.0.0.0:8080/x")]
    public async Task ValidateOutboundUrlAsync_blocks_dangerous_literals(string url)
        => (await SsrfGuard.ValidateOutboundUrlAsync(url)).Should().NotBeNull();

    [Theory]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("https://192.168.1.10:9000/events")]
    public async Task ValidateOutboundUrlAsync_allows_internal_hosts(string url)
        => (await SsrfGuard.ValidateOutboundUrlAsync(url)).Should().BeNull(
            "on-prem webhook receivers legitimately live on private networks");

    [Theory]
    [InlineData("ftp://example.com/x")]        // disallowed scheme
    [InlineData("file:///etc/passwd")]         // disallowed scheme
    [InlineData("not-a-url")]                  // not absolute
    [InlineData("")]                           // empty
    public async Task ValidateOutboundUrlAsync_rejects_bad_schemes_and_malformed(string url)
        => (await SsrfGuard.ValidateOutboundUrlAsync(url)).Should().NotBeNull();

    [Fact]
    public async Task ValidateOutboundUrlAsync_blocks_localhost_name()
        => (await SsrfGuard.ValidateOutboundUrlAsync("http://localhost:5000/hook"))
            .Should().NotBeNull("localhost resolves to loopback");
}

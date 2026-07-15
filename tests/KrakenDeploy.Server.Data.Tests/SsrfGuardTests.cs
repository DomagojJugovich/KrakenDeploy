using System.Net;
using FluentAssertions;
using KrakenDeploy.Server.Data.Net;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests for the policy-aware outbound-URL SSRF guard (A6 / T1-11). The default
/// posture is deny: loopback and private ranges are refused unless the policy
/// opts in; link-local/metadata and unspecified are hard-blocked and never
/// allowlistable. Pure unit tests — literal IPs only, so no DNS / network.
/// </summary>
public sealed class SsrfGuardTests
{
    private static readonly SsrfPolicy DenyAll = new();
    private static readonly SsrfPolicy AllowLoopback = new() { AllowLoopback = true };
    private static readonly SsrfPolicy AllowPrivate = new() { AllowPrivate = true };

    // ── Address classification ───────────────────────────────────────────────

    [Theory]
    [InlineData("169.254.0.1", true)]           // link-local
    [InlineData("169.254.169.254", true)]       // cloud metadata
    [InlineData("fe80::1", true)]               // IPv6 link-local
    [InlineData("0.0.0.0", true)]               // unspecified
    [InlineData("::", true)]                     // IPv6 unspecified
    [InlineData("127.0.0.1", false)]            // loopback is policy-gated, not hard-blocked
    [InlineData("10.0.0.5", false)]             // private is policy-gated
    [InlineData("8.8.8.8", false)]              // public
    public void IsHardBlocked_flags_only_metadata_linklocal_unspecified(string ip, bool blocked)
        => SsrfGuard.IsHardBlocked(IPAddress.Parse(ip)).Should().Be(blocked);

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]             // 127.0.0.0/8 entirely
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]      // IPv4-mapped loopback
    [InlineData("10.0.0.1", false)]
    public void IsLoopback_classifies(string ip, bool loopback)
        => SsrfGuard.IsLoopback(IPAddress.Parse(ip)).Should().Be(loopback);

    [Theory]
    [InlineData("10.0.0.5", true)]              // RFC1918
    [InlineData("172.16.4.4", true)]            // RFC1918
    [InlineData("172.31.255.255", true)]        // RFC1918 top of /12
    [InlineData("172.32.0.1", false)]           // just outside 172.16/12
    [InlineData("192.168.1.10", true)]          // RFC1918
    [InlineData("100.64.0.1", true)]            // CGNAT 100.64/10
    [InlineData("100.128.0.1", false)]          // just outside CGNAT
    [InlineData("fd00::1", true)]               // IPv6 ULA
    [InlineData("fc00::1", true)]               // IPv6 ULA
    [InlineData("8.8.8.8", false)]              // public
    [InlineData("2001:4860:4860::8888", false)] // public IPv6
    public void IsPrivate_classifies(string ip, bool priv)
        => SsrfGuard.IsPrivate(IPAddress.Parse(ip)).Should().Be(priv);

    // ── EvaluateAddress: policy gating ───────────────────────────────────────

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    public void EvaluateAddress_hard_blocks_are_never_allowlistable(string ip)
    {
        // Even a policy that allows everything + lists the host cannot re-enable
        // link-local/metadata/unspecified.
        var permissive = new SsrfPolicy
        {
            AllowLoopback = true,
            AllowPrivate = true,
            AllowedHosts = [ip, "0.0.0.0/0", "::/0"],
        };
        SsrfGuard.EvaluateAddress(IPAddress.Parse(ip), ip, permissive)
            .Should().NotBeNull();
    }

    [Fact]
    public void EvaluateAddress_denies_loopback_by_default_allows_when_opted_in()
    {
        var loop = IPAddress.Parse("127.0.0.1");
        SsrfGuard.EvaluateAddress(loop, "localhost", DenyAll).Should().NotBeNull();
        SsrfGuard.EvaluateAddress(loop, "localhost", AllowLoopback).Should().BeNull();
    }

    [Fact]
    public void EvaluateAddress_denies_private_by_default_allows_when_opted_in()
    {
        var priv = IPAddress.Parse("10.0.0.5");
        SsrfGuard.EvaluateAddress(priv, "internal", DenyAll).Should().NotBeNull();
        SsrfGuard.EvaluateAddress(priv, "internal", AllowPrivate).Should().BeNull();
    }

    [Fact]
    public void EvaluateAddress_allows_public_under_deny_all()
        => SsrfGuard.EvaluateAddress(IPAddress.Parse("8.8.8.8"), "dns.google", DenyAll)
            .Should().BeNull();

    // ── Allowlist matching (host / IP / CIDR) ────────────────────────────────

    [Fact]
    public void EvaluateAddress_allowlist_by_exact_ip()
    {
        var policy = new SsrfPolicy { AllowedHosts = ["10.0.0.5"] };
        SsrfGuard.EvaluateAddress(IPAddress.Parse("10.0.0.5"), "x", policy).Should().BeNull();
        SsrfGuard.EvaluateAddress(IPAddress.Parse("10.0.0.6"), "x", policy).Should().NotBeNull();
    }

    [Fact]
    public void EvaluateAddress_allowlist_by_cidr()
    {
        var policy = new SsrfPolicy { AllowedHosts = ["192.168.16.0/24"] };
        SsrfGuard.EvaluateAddress(IPAddress.Parse("192.168.16.200"), "x", policy).Should().BeNull();
        SsrfGuard.EvaluateAddress(IPAddress.Parse("192.168.17.1"), "x", policy).Should().NotBeNull();
    }

    [Fact]
    public void EvaluateAddress_allowlist_by_hostname_is_case_insensitive()
    {
        var policy = new SsrfPolicy { AllowedHosts = ["Hooks.Intranet.Example"] };
        SsrfGuard.EvaluateAddress(IPAddress.Parse("10.9.9.9"), "hooks.intranet.example", policy)
            .Should().BeNull();
        SsrfGuard.EvaluateAddress(IPAddress.Parse("10.9.9.9"), "other.host", policy)
            .Should().NotBeNull();
    }

    // ── ValidateOutboundUrlAsync (literal IPs → no DNS) ───────────────────────

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("http://0.0.0.0:8080/x")]
    [InlineData("http://10.0.0.5/hook")]         // RFC1918 denied by default
    [InlineData("https://192.168.1.10:9000/x")]  // RFC1918 denied by default
    public async Task ValidateOutboundUrlAsync_denies_blocked_literals_under_default_policy(string url)
        => (await SsrfGuard.ValidateOutboundUrlAsync(url, DenyAll)).Should().NotBeNull();

    [Theory]
    [InlineData("http://8.8.8.8/hook")]
    [InlineData("https://203.0.113.10:9000/events")]
    public async Task ValidateOutboundUrlAsync_allows_public_literals(string url)
        => (await SsrfGuard.ValidateOutboundUrlAsync(url, DenyAll)).Should().BeNull();

    [Fact]
    public async Task ValidateOutboundUrlAsync_allows_rfc1918_only_when_allowlisted()
    {
        // Acceptance: a webhook to an RFC1918 host is denied unless allowlisted.
        (await SsrfGuard.ValidateOutboundUrlAsync("http://10.0.0.5/hook", DenyAll))
            .Should().NotBeNull();

        var allow = new SsrfPolicy { AllowedHosts = ["10.0.0.0/8"] };
        (await SsrfGuard.ValidateOutboundUrlAsync("http://10.0.0.5/hook", allow))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("ftp://example.com/x")]          // disallowed scheme
    [InlineData("file:///etc/passwd")]           // disallowed scheme
    [InlineData("not-a-url")]                     // not absolute
    [InlineData("")]                              // empty
    public async Task ValidateOutboundUrlAsync_rejects_bad_schemes_and_malformed(string url)
        => (await SsrfGuard.ValidateOutboundUrlAsync(url, AllowLoopback)).Should().NotBeNull();
}

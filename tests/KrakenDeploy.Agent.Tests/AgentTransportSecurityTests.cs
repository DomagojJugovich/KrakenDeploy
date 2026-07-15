using FluentAssertions;
using KrakenDeploy.Agent.Transport;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// A8/T1-12 — the agent's transport-security policy. https is required for the
/// server URL (which backs both the SignalR tunnel and the gRPC channels);
/// cleartext http:// is refused unless the explicit dev override is set. The
/// channel factory turns a policy failure into a hard exception so a misconfigured
/// agent cannot silently downgrade to cleartext HTTP/2.
/// </summary>
public sealed class AgentTransportSecurityTests
{
    [Fact]
    public void Https_is_always_allowed()
    {
        AgentTransportSecurity.Validate("https://deploy.example.com", allowInsecureHttp: false)
            .Ok.Should().BeTrue();
    }

    [Fact]
    public void Http_is_refused_without_the_override()
    {
        var (ok, error) = AgentTransportSecurity.Validate("http://deploy.example.com", allowInsecureHttp: false);

        ok.Should().BeFalse();
        error.Should().Contain("https");
    }

    [Fact]
    public void Http_is_allowed_only_with_the_override()
    {
        AgentTransportSecurity.Validate("http://localhost:5000", allowInsecureHttp: true)
            .Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("deploy.example.com")] // no scheme
    [InlineData("ftp://deploy.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_or_unsupported_urls_are_refused(string? url)
    {
        AgentTransportSecurity.Validate(url, allowInsecureHttp: true).Ok.Should().BeFalse();
    }

    [Fact]
    public void Factory_throws_on_a_disallowed_cleartext_url()
    {
        var act = () => GrpcChannelFactory.Create("http://deploy.example.com", "token", allowInsecureHttp: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    [Fact]
    public void Factory_builds_a_channel_for_https()
    {
        // ForAddress is lazy (no connection), so this exercises the guard + build path.
        using var channel = GrpcChannelFactory.Create("https://deploy.example.com", "token", allowInsecureHttp: false);

        channel.Should().NotBeNull();
    }
}

using System.Text;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Agent.Services;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// A8 sliding refresh — the schedule that decides WHEN the agent renews its
/// bearer token: parse the validity window straight from the JWT payload and
/// refresh once past half-life. Malformed tokens must fail the parse (the
/// service then refreshes eagerly rather than never).
/// </summary>
public sealed class AgentTokenRefreshPolicyTests
{
    private static readonly DateTimeOffset Nbf = new(2026, 07, 01, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Exp = Nbf.AddDays(90);

    [Fact]
    public void Parses_the_validity_window_from_a_compact_jwt()
    {
        var ok = AgentTokenRefreshPolicy.TryGetValidityWindow(
            MakeJwt(Nbf, Exp), out var nbf, out var exp);

        ok.Should().BeTrue();
        nbf.Should().Be(Nbf);
        exp.Should().Be(Exp);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!notbase64url!!!.c")]
    [InlineData("")]
    public void Malformed_tokens_fail_the_parse(string token)
    {
        AgentTokenRefreshPolicy.TryGetValidityWindow(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Token_without_exp_or_nbf_fails_the_parse()
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { sub = "x" }));
        AgentTokenRefreshPolicy.TryGetValidityWindow($"e30.{payload}.sig", out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Does_not_refresh_before_half_life()
    {
        var justBeforeHalf = Nbf.AddDays(45).AddSeconds(-1);

        AgentTokenRefreshPolicy.ShouldRefresh(justBeforeHalf, Nbf, Exp).Should().BeFalse();
    }

    [Theory]
    [InlineData(45)]   // exactly half-life
    [InlineData(60)]   // deep in the second half
    [InlineData(120)]  // already past expiry — refresh attempt surfaces the 401
    public void Refreshes_from_half_life_onwards(int daysAfterNbf)
    {
        AgentTokenRefreshPolicy.ShouldRefresh(Nbf.AddDays(daysAfterNbf), Nbf, Exp)
            .Should().BeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string MakeJwt(DateTimeOffset nbf, DateTimeOffset exp)
    {
        // Header/signature content is irrelevant to the parser — only the payload
        // segment is read, exactly as with a real server-issued token.
        var header = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            nbf = nbf.ToUnixTimeSeconds(),
            exp = exp.ToUnixTimeSeconds(),
        }));
        return $"{header}.{payload}.signature";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

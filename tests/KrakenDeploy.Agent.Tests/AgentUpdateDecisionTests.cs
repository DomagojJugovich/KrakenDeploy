using FluentAssertions;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Contracts;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// C6 — the pure decision logic the self-upgrade service relies on: whether an
/// offered update is verifiable + contract-compatible (<see cref="AgentUpdateService.EvaluateOffer"/>)
/// and whether a swap may proceed right now (<see cref="AgentUpdateService.CanSwapNow"/>).
/// </summary>
public sealed class AgentUpdateDecisionTests
{
    private static AgentUpdateInfo Offer(
        bool available = true,
        string? url = "/api/agents/download/win-x64",
        string? sha = "abc123",
        int serverContract = 1,
        int? targetContract = 1)
        => new(available, "1.2.4", url, 1024, sha, serverContract, targetContract);

    [Fact]
    public void EvaluateOffer_no_update_when_unavailable()
        => AgentUpdateService.EvaluateOffer(Offer(available: false))
            .Should().Be(AgentUpdateService.UpdateDecision.NoUpdate);

    [Fact]
    public void EvaluateOffer_no_update_when_download_url_missing()
        => AgentUpdateService.EvaluateOffer(Offer(url: null))
            .Should().Be(AgentUpdateService.UpdateDecision.NoUpdate);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EvaluateOffer_refuses_update_without_a_hash(string? sha)
        => AgentUpdateService.EvaluateOffer(Offer(sha: sha))
            .Should().Be(AgentUpdateService.UpdateDecision.HashMissing);

    [Fact]
    public void EvaluateOffer_refuses_contract_skew()
        => AgentUpdateService.EvaluateOffer(Offer(serverContract: 1, targetContract: 2))
            .Should().Be(AgentUpdateService.UpdateDecision.ContractSkew);

    [Fact]
    public void EvaluateOffer_refuses_undeclared_target_contract()
        => AgentUpdateService.EvaluateOffer(Offer(targetContract: null))
            .Should().Be(AgentUpdateService.UpdateDecision.ContractSkew);

    [Fact]
    public void EvaluateOffer_proceeds_when_verifiable_and_matched()
        => AgentUpdateService.EvaluateOffer(Offer(serverContract: 1, targetContract: 1))
            .Should().Be(AgentUpdateService.UpdateDecision.Proceed);

    // ── CanSwapNow ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true,  false, true,  true)]   // in window, idle, connected → swap
    [InlineData(false, false, true,  false)]  // outside window → wait
    [InlineData(true,  true,  true,  false)]  // deployment in flight → no swap
    [InlineData(true,  false, false, false)]  // disconnected → no swap
    public void CanSwapNow_truth_table(
        bool inWindow, bool inFlight, bool connected, bool expected)
        => AgentUpdateService.CanSwapNow(inWindow, inFlight, connected).Should().Be(expected);

    // ── AttemptsExhausted (crash-loop bound) ─────────────────────────────────

    [Theory]
    [InlineData(0, 3, false)]  // first probe → keep trying
    [InlineData(2, 3, false)]  // third probe → keep trying
    [InlineData(3, 3, true)]   // used == max → roll back
    [InlineData(4, 3, true)]   // over max → roll back
    [InlineData(0, 0, false)]  // misconfigured max floored to 1: attempt 0 still probes
    [InlineData(1, 0, true)]   // misconfigured max floored to 1: attempt 1 rolls back
    [InlineData(1, -5, true)]  // negative max floored to 1
    public void AttemptsExhausted_boundary(int used, int max, bool expected)
        => AgentUpdateService.AttemptsExhausted(used, max).Should().Be(expected);

    // ── IsAgentApphost (swap-target guard, fix #2) ───────────────────────────

    [Theory]
    [InlineData("/opt/kraken/KrakenDeploy.Agent", true)]                    // Linux apphost
    [InlineData("C:\\Kraken\\KrakenDeploy.Agent.exe", true)]                // Windows apphost
    [InlineData("C:\\Kraken\\krakendeploy.agent.exe", true)]                // non-canonical casing (the regression)
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe", false)]            // muxer launch
    [InlineData("/usr/bin/dotnet", false)]                                  // muxer launch (Linux)
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAgentApphost_classifies_launch(string? processPath, bool expected)
        => AgentUpdateService.IsAgentApphost(processPath).Should().Be(expected);
}

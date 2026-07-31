using FluentAssertions;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
    // Normal operation: all three of window / idle / connected are required.
    [InlineData(true,  false, true,  false, true)]   // in window, idle, connected → swap
    [InlineData(false, false, true,  false, false)]  // outside window → wait
    [InlineData(true,  true,  true,  false, false)]  // deployment in flight → no swap
    [InlineData(true,  false, false, false, false)]  // disconnected → no swap
    // Wire-contract refusal: the window and the connected term are bypassed, because both
    // are unreachable-by-construction in that state and gating on them is a DEADLOCK — the
    // server answers 426 on the handshake, so IsConnected can never become true, so the
    // binary that would fix the refusal is never installed.
    [InlineData(false, false, false, true,  true)]   // refused, idle → swap anyway
    [InlineData(true,  false, false, true,  true)]   // refused, in window, disconnected → swap
    // …but in-flight work is NOT bypassed. A deployment that started before the server was
    // upgraded keeps running locally, and replacing the install directory under it is the one
    // thing the refusal does not excuse.
    [InlineData(false, true,  false, true,  false)]  // refused but busy → still no swap
    [InlineData(true,  true,  true,  true,  false)]  // refused, everything else fine, busy
    public void CanSwapNow_truth_table(
        bool inWindow, bool inFlight, bool connected, bool contractRefused, bool expected)
        => AgentUpdateService.CanSwapNow(inWindow, inFlight, connected, contractRefused)
            .Should().Be(expected);

    // ── Remote text is bounded before it is re-published ─────────────────────

    [Fact]
    public void Truncate_bounds_remote_detail_and_says_so()
    {
        // The server's task-in-flight Detail is forwarded into the agent log AND back to the
        // server as an update-status detail, which lands in the audit log and is then pushed to
        // the webhook, e-mail and AI-inspect transports — the last interpolating it into an LLM
        // prompt. Bounding it where it enters the agent keeps every downstream copy bounded.
        var trimmed = AgentUpdateService.Truncate(
            new string('x', 5_000), AgentUpdateService.MaxRemoteDetailLength)!;

        trimmed.Should().StartWith(new string('x', AgentUpdateService.MaxRemoteDetailLength));
        trimmed.Should().Contain("truncated from 5000");
        trimmed.Length.Should().BeLessThan(AgentUpdateService.MaxRemoteDetailLength + 40);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wave 2 of task 7 is still running")]
    public void Truncate_leaves_short_or_absent_detail_alone(string? value)
        => AgentUpdateService.Truncate(value, AgentUpdateService.MaxRemoteDetailLength)
            .Should().Be(value);

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

    // ── F5: SwapGateTimeout validation ──────────────────────────────────────
    //
    // The knob bounds how long a QUEUED exclusive writer may block every new deployment
    // and ad-hoc script on the box. Every malformed value defeats exactly that bound,
    // and the agent had no options validation at all before F5.

    [Theory]
    // A bare number binds as DAYS — five days of whole-machine blockage, and small
    // enough to slip under the gate's own ~24.8-day CancelAfter clamp.
    [InlineData("5")]
    [InlineData("30")]
    // -1 ms IS Timeout.InfiniteTimeSpan: the wait is never bounded, so the writer parks
    // forever and the agent silently accepts no work until it is restarted.
    [InlineData("-00:00:00.001")]
    [InlineData("-00:05:00")]
    // Zero degrades the acquisition to a non-blocking probe, so the block-new-work
    // guarantee never engages at all.
    [InlineData("00:00:00")]
    // Above the ceiling: a swap window measured in hours has already failed its purpose.
    [InlineData("06:00:00")]
    public void SwapGateTimeout_rejects_values_that_defeat_the_bound(string configured)
        => ValidateSwapGateTimeout(configured).Succeeded.Should().BeFalse(
            $"'{configured}' must not reach the machine gate");

    [Theory]
    [InlineData("00:02:00")]
    [InlineData("00:00:30")]
    public void SwapGateTimeout_accepts_a_sane_duration(string configured)
        => ValidateSwapGateTimeout(configured).Succeeded.Should().BeTrue();

    [Theory]
    // A long poll interval is a legitimate operator choice on a metered or segmented
    // link, and it is the SAFE direction for the SwapGateTimeout < CheckInterval rule.
    // An earlier cut applied the swap window's 1-hour ceiling to this too, which turned
    // a twice-daily poll into a fleet-wide boot failure on an unreachable agent.
    [InlineData("Agent:Update:CheckInterval", "06:00:00")]
    // A whole day must be written 1.00:00:00 — "24:00:00" is not a valid hh:mm:ss, so the
    // binder rejects it. That is the same unit trap the bare-number rule guards.
    [InlineData("Agent:Update:CheckInterval", "1.00:00:00")]
    [InlineData("Agent:Update:HealthCheckTimeout", "02:00:00")]
    public void Long_non_blocking_durations_are_accepted(string key, string configured)
        => Validate(new Dictionary<string, string?> { [key] = configured })
            .Succeeded.Should().BeTrue(
                $"'{key}' does not block the machine, so the swap window's ceiling must not apply");

    [Theory]
    // The bare-number and non-positive rules DO apply to every duration.
    [InlineData("Agent:Update:CheckInterval", "5")]
    [InlineData("Agent:Update:CheckInterval", "00:00:00")]
    [InlineData("Agent:Update:HealthCheckTimeout", "3")]
    [InlineData("Agent:Update:HealthCheckTimeout", "-00:01:00")]
    public void Malformed_non_blocking_durations_are_still_rejected(string key, string configured)
        => Validate(new Dictionary<string, string?> { [key] = configured })
            .Succeeded.Should().BeFalse();

    [Fact]
    public void A_disabled_updater_is_not_validated()
    {
        // ValidateOnStart makes every failure a BOOT failure, and an agent that will not
        // boot cannot be reached or self-upgrade out of the problem. None of these knobs
        // is read when the updater is off, so a legacy value must not brick that agent.
        var result = Validate(new Dictionary<string, string?>
        {
            ["Agent:Update:Enabled"] = "false",
            ["Agent:Update:SwapGateTimeout"] = "5",     // five DAYS
            ["Agent:Update:CheckInterval"] = "00:00:00",
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void The_cross_field_failure_names_both_keys()
    {
        // The operator may have set only CheckInterval — with SwapGateTimeout defaulting
        // to 2 min, tightening the interval to 2 min trips this. An error naming only
        // SwapGateTimeout would send them to a key they never touched.
        var message = Validate(new Dictionary<string, string?>
        {
            ["Agent:Update:CheckInterval"] = "00:02:00",
        }).FailureMessage;

        message.Should().Contain(nameof(AgentUpdateConfig.SwapGateTimeout));
        message.Should().Contain(nameof(AgentUpdateConfig.CheckInterval));
    }

    [Fact]
    public void SwapGateTimeout_must_be_shorter_than_the_check_interval()
    {
        // At equal values PeriodicTimer has a coalesced tick ready the instant the wait
        // expires, so the updater re-queues a machine-blocking writer back-to-back for
        // the whole maintenance window without ever completing a swap. The originally
        // shipped defaults were both 5 min.
        var result = Validate(new Dictionary<string, string?>
        {
            ["Agent:Update:SwapGateTimeout"] = "00:05:00",
            ["Agent:Update:CheckInterval"] = "00:05:00",
        });

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("shorter than");
    }

    [Fact]
    public void The_shipped_defaults_validate()
        => Validate([]).Succeeded.Should().BeTrue(
            "a fresh agent with no Agent:Update configuration at all must boot");

    private static ValidateOptionsResult ValidateSwapGateTimeout(string configured)
        => Validate(new Dictionary<string, string?>
        {
            ["Agent:Update:SwapGateTimeout"] = configured,
        });

    private static ValidateOptionsResult Validate(
        IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new AgentUpdateConfig();
        config.GetSection(AgentUpdateConfigValidator.SectionName).Bind(options);
        return new AgentUpdateConfigValidator(config).Validate(name: null, options);
    }
}

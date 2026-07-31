using System.Net;
using FluentAssertions;
using KrakenDeploy.Agent.Transport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// B2/T0-2 — the reconnect policy must be UNBOUNDED (never return null = never
/// stop trying), jittered, capped, and must shift to a slow fixed lane on
/// 401/403 (revoked/expired token) instead of hammering doomed negotiates.
/// </summary>
public sealed class AgentReconnectPolicyTests
{
    private static AgentReconnectPolicy Policy(double jitter = 1.0)
        => new(NullLogger.Instance, () => jitter);

    private static RetryContext Context(long previousRetryCount, Exception? reason = null)
        => new()
        {
            PreviousRetryCount = previousRetryCount,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = reason ?? new IOException("connection reset"),
        };

    [Fact]
    public void First_attempt_is_immediate()
    {
        Policy().NextRetryDelay(Context(0)).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 2.0)]
    [InlineData(3, 4.0)]
    [InlineData(4, 8.0)]
    [InlineData(5, 16.0)]
    public void Backoff_doubles_up_to_the_ceiling(long attempt, double expectedCeilingSeconds)
    {
        // jitter = 1.0 makes the delay equal its ceiling.
        Policy().NextRetryDelay(Context(attempt))
            .Should().Be(TimeSpan.FromSeconds(expectedCeilingSeconds));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(long.MaxValue - 1)]
    public void Backoff_saturates_at_the_30s_cap(long attempt)
    {
        Policy().NextRetryDelay(Context(attempt))
            .Should().Be(AgentReconnectPolicy.MaxDelay);
    }

    [Fact]
    public void Policy_is_unbounded_and_never_gives_up()
    {
        var policy = Policy(jitter: 0.5);

        for (long attempt = 0; attempt < 100_000; attempt += 997)
        {
            policy.NextRetryDelay(Context(attempt)).Should().NotBeNull();
        }
    }

    [Fact]
    public void Jitter_scales_the_delay_below_the_ceiling()
    {
        Policy(jitter: 0.5).NextRetryDelay(Context(100))
            .Should().Be(TimeSpan.FromSeconds(15));

        // Full jitter may land at (near) zero — that is by design.
        Policy(jitter: 0.0).NextRetryDelay(Context(100))
            .Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Auth_failure_moves_to_the_slow_lane_but_keeps_trying(HttpStatusCode status)
    {
        var policy = Policy();
        var authFailure = new HttpRequestException("rejected", inner: null, statusCode: status);

        policy.NextRetryDelay(Context(7, authFailure))
            .Should().Be(AgentReconnectPolicy.OperatorActionDelay);

        // Still unbounded in the auth lane.
        policy.NextRetryDelay(Context(5_000, authFailure))
            .Should().Be(AgentReconnectPolicy.OperatorActionDelay);
    }

    [Fact]
    public void Recovery_from_auth_failure_returns_to_the_normal_lane()
    {
        var policy = Policy();
        var authFailure = new HttpRequestException(
            "rejected", inner: null, statusCode: HttpStatusCode.Unauthorized);

        policy.NextRetryDelay(Context(3, authFailure))
            .Should().Be(AgentReconnectPolicy.OperatorActionDelay);

        // Next attempt fails with an ordinary transport error — normal pacing resumes.
        policy.NextRetryDelay(Context(1))
            .Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Non_auth_http_errors_use_the_normal_lane()
    {
        var serverError = new HttpRequestException(
            "boom", inner: null, statusCode: HttpStatusCode.InternalServerError);

        Policy().NextRetryDelay(Context(2, serverError))
            .Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_426_contract_refusal_takes_the_operator_action_lane()
    {
        // The gate's own log line used to claim the agent "will retry on the slow lane", and it
        // was wrong: IsAuthFailure matched only 401/403, so a 426 rode the normal exponential
        // lane capped at 30 s. On a 1 000-agent fleet that is ~33 negotiates per second,
        // indefinitely, each one costing the server a log line and an audit row.
        var refusal = new HttpRequestException(
            "Response status code does not indicate success: 426 (Upgrade Required).",
            inner: null, statusCode: HttpStatusCode.UpgradeRequired);

        Policy().NextRetryDelay(Context(1, refusal))
            .Should().Be(AgentReconnectPolicy.OperatorActionDelay);
    }

    [Fact]
    public void The_contract_lane_and_the_credential_lane_log_separately()
    {
        // Same delay, DIFFERENT remedy — 401/403 means "re-enroll this agent", 426 means
        // "upgrade the agent binary". They must not collapse into one message: sending an
        // operator to re-enroll a fleet whose real problem is a contract bump costs an outage.
        // Asserted through the log because the delay alone cannot tell them apart.
        using var log = new ListLoggerProvider();
        var policy = new AgentReconnectPolicy(log.CreateLogger("policy"), () => 1.0);

        policy.NextRetryDelay(Context(1, new HttpRequestException(
            "no", inner: null, statusCode: HttpStatusCode.Unauthorized)));
        policy.NextRetryDelay(Context(2, new HttpRequestException(
            "no", inner: null, statusCode: HttpStatusCode.UpgradeRequired)));

        var errors = log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).ToList();
        errors.Should().HaveCount(2, "a change of cause must re-log — it is the operator's " +
            "signal that their previous fix took effect, or that a second problem is now first");
        errors[0].Should().Contain("RE-ENROLL").And.NotContain("BINARY UPGRADE");
        errors[1].Should().Contain("BINARY UPGRADE").And.NotContain("RE-ENROLL");
    }

    [Fact]
    public void A_repeated_cause_logs_once_per_streak()
    {
        using var log = new ListLoggerProvider();
        var policy = new AgentReconnectPolicy(log.CreateLogger("policy"), () => 1.0);
        var refusal = new HttpRequestException(
            "no", inner: null, statusCode: HttpStatusCode.UpgradeRequired);

        for (var i = 0; i < 5; i++)
        {
            policy.NextRetryDelay(Context(i, refusal));
        }

        log.Entries.Count(e => e.Level >= LogLevel.Error).Should().Be(1,
            "once per streak, not once per 5-minute attempt");
    }

    // The "churn lane" that used to live here — a count of consecutive short-lived
    // connections, meant to pace a server that repeatedly rejects the agent — is gone, and so
    // are its three tests. It could not reach the failure it was written for: a rejection
    // inside OnConnectedAsync fires Closed rather than Reconnecting, so the event that fed the
    // counter never raised, and for the drop it COULD see, HubConnection computes the delay
    // before raising Reconnecting, so the counter lagged by an episode. Both facts are now
    // pinned by execution in ReconnectE2ETests, and the pacing lives in the only loop that
    // observes a permanent close — ServerLinkHostedService's supervision loop, covered by
    // ServerLinkHostedServiceTests.
}

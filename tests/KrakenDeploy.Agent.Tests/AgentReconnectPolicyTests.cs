using System.Net;
using FluentAssertions;
using KrakenDeploy.Agent.Transport;
using Microsoft.AspNetCore.SignalR.Client;
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
            .Should().Be(AgentReconnectPolicy.AuthFailureDelay);

        // Still unbounded in the auth lane.
        policy.NextRetryDelay(Context(5_000, authFailure))
            .Should().Be(AgentReconnectPolicy.AuthFailureDelay);
    }

    [Fact]
    public void Recovery_from_auth_failure_returns_to_the_normal_lane()
    {
        var policy = Policy();
        var authFailure = new HttpRequestException(
            "rejected", inner: null, statusCode: HttpStatusCode.Unauthorized);

        policy.NextRetryDelay(Context(3, authFailure))
            .Should().Be(AgentReconnectPolicy.AuthFailureDelay);

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

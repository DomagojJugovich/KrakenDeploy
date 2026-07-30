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

    // ── Churn lane: connections that die before they are useful ──────────────

    [Fact]
    public void Repeatedly_aborted_connections_escalate_although_each_episode_restarts_at_zero()
    {
        // The defect this closes. PreviousRetryCount restarts at 0 for every reconnect
        // EPISODE and attempt 0 is deliberately immediate, so a connection the server aborts
        // moments after accepting it reconnects at round-trip cadence forever. The hub does
        // exactly that for a deleted or retired target, and Context.Abort() drops the
        // transport rather than closing it, so the client sees each abort as a fresh blip.
        var clock = new StubClock();
        var policy = new AgentReconnectPolicy(NullLogger.Instance, () => 1.0, clock);

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 4; i++)
        {
            policy.NoteConnected();
            clock.Advance(TimeSpan.FromMilliseconds(20)); // aborted almost immediately
            policy.NoteConnectionLost();
            delays.Add(policy.NextRetryDelay(Context(0))!.Value);
        }

        // Full exponential escalation from the first instant abort onward. A connection that
        // died in 20 ms is not the clean drop of a healthy link, so it does NOT get attempt
        // zero's immediate retry — that concession is reserved for a connection that actually
        // worked (see One_useful_connection_clears_the_churn).
        delays.Should().Equal(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void One_useful_connection_clears_the_churn()
    {
        // A healthy agent that loses its link must still reconnect immediately — that is what
        // attempt 0 being TimeSpan.Zero is for, and the churn lane must not take it away.
        var clock = new StubClock();
        var policy = new AgentReconnectPolicy(NullLogger.Instance, () => 1.0, clock);

        for (var i = 0; i < 3; i++)
        {
            policy.NoteConnected();
            clock.Advance(TimeSpan.FromMilliseconds(20));
            policy.NoteConnectionLost();
            policy.NextRetryDelay(Context(0));
        }

        policy.NoteConnected();
        clock.Advance(AgentReconnectPolicy.MinUsefulConnection + TimeSpan.FromSeconds(1));
        policy.NoteConnectionLost();

        policy.NextRetryDelay(Context(0)).Should().Be(TimeSpan.Zero,
            "the link demonstrably worked, so the next clean drop is a blip again");
    }

    [Fact]
    public void Churn_never_shortens_the_within_episode_backoff()
    {
        // The two counts combine with Math.Max, so an episode's own escalation cannot be
        // undone by an idle churn counter.
        var policy = new AgentReconnectPolicy(NullLogger.Instance, () => 1.0, new StubClock());

        policy.NextRetryDelay(Context(5)).Should().Be(TimeSpan.FromSeconds(16));
    }

    private sealed class StubClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }
}

using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// F2-followup 6 — the execution-budget re-arm is clamped to the dispatch backstop,
/// so a late "I acquired the machine gate" report can shorten the attempt's deadline
/// but never lengthen it past the ceiling the operator configured.
/// <para>
/// The arithmetic is tested directly rather than through a wall-clock deployment:
/// <c>CancellationTokenSource.CancelAfter</c> always uses the system timer (there is
/// no <c>TimeProvider</c> overload, and none on <c>CreateLinkedTokenSource</c>), so an
/// integration test of the clamp would have to be a multi-second race. The wiring —
/// that the report re-arms at all, and that the re-arm tightens — is already covered
/// by <see cref="WaveDeadlineArmingTests"/> against the real worker.
/// </para>
/// </summary>
public sealed class WaveDeadlineClampTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(30);

    [Fact]
    public void A_prompt_report_gets_the_whole_execution_budget()
    {
        // The normal case: the agent took the gate well inside the queue-wait
        // ceiling, so most of the backstop is still unspent and the clamp is inert.
        var window = DeploymentWorker.WaveDeadline.ComputeArmWindow(
            Budget, remainingToBackstop: TimeSpan.FromHours(2));

        window.Should().Be(Budget);
    }

    [Fact]
    public void A_report_with_exactly_the_budget_left_still_gets_the_whole_budget()
    {
        // Boundary: equal values must not be treated as "over" and shaved.
        var window = DeploymentWorker.WaveDeadline.ComputeArmWindow(
            Budget, remainingToBackstop: Budget);

        window.Should().Be(Budget);
    }

    [Fact]
    public void A_late_report_is_clamped_to_what_is_left_of_the_backstop()
    {
        // THE regression. Pre-followup this returned the full 30 min, extending an
        // attempt that was 10 s from its ceiling by another half hour.
        var window = DeploymentWorker.WaveDeadline.ComputeArmWindow(
            Budget, remainingToBackstop: TimeSpan.FromSeconds(10));

        window.Should().Be(TimeSpan.FromSeconds(10),
            "the re-arm may tighten the deadline, never push it past the backstop");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void A_report_at_or_past_the_backstop_cancels_instead_of_rescheduling(int remainingSeconds)
    {
        // Non-positive delays make CancelAfter throw ArgumentOutOfRangeException, so
        // this case must be routed to Cancel() rather than clamped to a negative.
        var window = DeploymentWorker.WaveDeadline.ComputeArmWindow(
            Budget, remainingToBackstop: TimeSpan.FromSeconds(remainingSeconds));

        window.Should().BeNull();
    }

    /// <summary>
    /// An explicit per-step <c>TimeoutSeconds</c> is honoured as-is (it deliberately
    /// escapes the engine ceiling), and it is an <see cref="int"/> of SECONDS — so a
    /// large one is decades, past what the timer can express. Pre-clamp that threw
    /// <see cref="ArgumentOutOfRangeException"/> and failed the wave AT DISPATCH with
    /// a raw "Parameter 'delay'", before anything reached the agent.
    /// </summary>
    [Fact]
    public void A_decade_long_step_timeout_is_capped_at_what_the_timer_can_express()
    {
        var absurd = TimeSpan.FromSeconds(int.MaxValue);   // ~68 years
        absurd.Should().BeGreaterThan(DeploymentWorker.WaveDeadline.MaxTimerDelay);

        var clamped = DeploymentWorker.WaveDeadline.ClampToTimerLimit(absurd);

        using var cts = new CancellationTokenSource();
        var act = () => cts.CancelAfter(clamped);
        act.Should().NotThrow();
        clamped.Should().Be(DeploymentWorker.WaveDeadline.MaxTimerDelay);
    }

    [Fact]
    public void An_ordinary_deadline_passes_the_cap_through_untouched()
    {
        DeploymentWorker.WaveDeadline.ClampToTimerLimit(TimeSpan.FromHours(3))
            .Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void The_clamp_never_returns_a_delay_CancelAfter_would_reject()
    {
        // Property check over the whole shape of the input space, including the
        // degenerate budgets a test or a mis-bound config could produce.
        TimeSpan[] budgets =
        [
            TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1),
            TimeSpan.FromDays(7), TimeSpan.MaxValue,
        ];
        TimeSpan[] remainings =
        [
            TimeSpan.MinValue, TimeSpan.FromSeconds(-1), TimeSpan.Zero,
            TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), TimeSpan.MaxValue,
        ];

        using var cts = new CancellationTokenSource();
        foreach (var budget in budgets)
        {
            foreach (var remaining in remainings)
            {
                var window = DeploymentWorker.WaveDeadline.ComputeArmWindow(budget, remaining);
                if (window is not { } delay)
                {
                    continue;
                }

                // CancelAfter's own precondition is 0 <= delay <= MaxSupportedTimeout;
                // zero means "cancel now" and is a legitimate answer for a zero
                // budget (unreachable in production, but a test may construct one).
                delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero,
                    $"budget {budget} with {remaining} left must not arm a negative delay");
                delay.Should().BeLessThanOrEqualTo(remaining,
                    $"budget {budget} with {remaining} left must not exceed the backstop");
                var act = () => cts.CancelAfter(delay);
                act.Should().NotThrow(
                    $"budget {budget} with {remaining} left produced a delay CancelAfter rejects");
            }
        }
    }
}

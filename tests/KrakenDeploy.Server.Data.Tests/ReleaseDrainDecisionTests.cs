using FluentAssertions;
using KrakenDeploy.Platform.Releases;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// The drain-and-retire rule (docs/blue-green-slot-deployment.md §9): in-flight
/// deployments always finish; the drain deadline applies to idle circuits only.
/// </summary>
public class ReleaseDrainDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_slot_retires()
        => ReleaseDrainDecision.ShouldRetire(Now, Now.AddHours(1), 0, 0).Should().BeTrue();

    [Fact]
    public void In_flight_deployment_blocks_retirement_even_past_the_deadline()
        => ReleaseDrainDecision.ShouldRetire(Now, Now.AddHours(-1), 0, 1).Should().BeFalse();

    [Fact]
    public void Idle_circuits_before_the_deadline_wait()
        => ReleaseDrainDecision.ShouldRetire(Now, Now.AddHours(1), 3, 0).Should().BeFalse();

    [Fact]
    public void Idle_circuits_past_the_deadline_retire()
        => ReleaseDrainDecision.ShouldRetire(Now, Now.AddHours(-1), 3, 0).Should().BeTrue();

    [Fact]
    public void Circuits_with_no_deadline_wait_indefinitely()
        => ReleaseDrainDecision.ShouldRetire(Now, null, 1, 0).Should().BeFalse();
}

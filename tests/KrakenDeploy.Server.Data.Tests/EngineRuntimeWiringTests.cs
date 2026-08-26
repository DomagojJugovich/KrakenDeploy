using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Data.Tests;

public sealed class EngineRuntimeWiringTests
{
    [Fact]
    public void Wave_without_explicit_rolling_cap_uses_default_fanout_of_ten()
    {
        var rolling = new RollingWindow(null, RollingCapReason.None, null);
        var cap = DeploymentWorker.ResolveTargetWaveMaxParallelism(rolling, 10);

        cap.Should().Be(10);
        RollingWindowResolver.Chunk(Enumerable.Range(1, 25).ToArray(), cap)
            .Select(batch => batch.Count)
            .Should().Equal(10, 10, 5);
    }

    [Fact]
    public void Valid_explicit_rolling_cap_overrides_default_fanout()
    {
        var rolling = new RollingWindow(3, RollingCapReason.Resolved, "Canary");
        var cap = DeploymentWorker.ResolveTargetWaveMaxParallelism(rolling, 10);

        cap.Should().Be(3);
        RollingWindowResolver.Chunk(Enumerable.Range(1, 8).ToArray(), cap)
            .Select(batch => batch.Count)
            .Should().Equal(3, 3, 2);
    }

    [Theory]
    [InlineData(RollingCapReason.Malformed)]
    [InlineData(RollingCapReason.MixedAncestors)]
    public void Invalid_explicit_rolling_ancestry_falls_back_to_default_fanout(
        RollingCapReason reason)
    {
        var rolling = new RollingWindow(null, reason, "Broken");

        DeploymentWorker.ResolveTargetWaveMaxParallelism(rolling, 10).Should().Be(10);
    }
}

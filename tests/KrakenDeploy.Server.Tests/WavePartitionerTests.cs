using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M14.4 <see cref="WavePartitioner"/>. Pin the wave-
/// formation contract: first step always opens a wave (its trigger is
/// ignored), <see cref="StepStartTrigger.StartWithPrevious"/> joins the
/// current wave, <see cref="StepStartTrigger.StartAfterPrevious"/> opens
/// the next wave. Mixed-side waves throw before construction.
/// </summary>
public sealed class WavePartitionerTests
{
    [Fact]
    public void Empty_input_yields_empty_partition()
    {
        var waves = WavePartitioner.Partition(
            steps: [],
            triggerByIndex: _ => StepStartTrigger.StartAfterPrevious);

        waves.Should().BeEmpty();
    }

    [Fact]
    public void Single_step_yields_one_single_step_wave()
    {
        var step = TargetStep(index: 0, name: "Deploy");

        var waves = WavePartitioner.Partition(
            steps: [step],
            triggerByIndex: _ => StepStartTrigger.StartAfterPrevious);

        waves.Should().HaveCount(1);
        waves[0].Kind.Should().Be(WavePartitioner.WaveKind.Target);
        waves[0].Steps.Should().BeEquivalentTo([step]);
    }

    [Fact]
    public void StartAfterPrevious_chain_yields_one_wave_per_step()
    {
        // Three target-side steps, all default trigger (StartAfterPrevious)
        // → three sequential waves, one step each. Pre-M14.4 default
        // behaviour is preserved exactly.
        var s0 = TargetStep(index: 0, name: "A");
        var s1 = TargetStep(index: 1, name: "B");
        var s2 = TargetStep(index: 2, name: "C");

        var waves = WavePartitioner.Partition(
            steps: [s0, s1, s2],
            triggerByIndex: _ => StepStartTrigger.StartAfterPrevious);

        waves.Should().HaveCount(3);
        waves.Select(w => w.Steps.Count).Should().Equal([1, 1, 1]);
        waves.Select(w => w.Steps[0].Name).Should().Equal(["A", "B", "C"]);
    }

    [Fact]
    public void StartWithPrevious_chain_collapses_into_one_wave()
    {
        // Four target-side steps where steps 1..3 are StartWithPrevious →
        // one wave of four. First step's trigger is ignored.
        var s0 = TargetStep(index: 0, name: "A");
        var s1 = TargetStep(index: 1, name: "B");
        var s2 = TargetStep(index: 2, name: "C");
        var s3 = TargetStep(index: 3, name: "D");

        var waves = WavePartitioner.Partition(
            steps: [s0, s1, s2, s3],
            triggerByIndex: idx => idx == 0
                ? StepStartTrigger.StartAfterPrevious
                : StepStartTrigger.StartWithPrevious);

        waves.Should().HaveCount(1);
        waves[0].Steps.Select(s => s.Name).Should().Equal(["A", "B", "C", "D"]);
    }

    [Fact]
    public void Mixed_StartTrigger_carves_waves_at_each_StartAfterPrevious()
    {
        // Pattern: A (after), B (with), C (after), D (with), E (with)
        // → [A,B] | [C,D,E]
        var steps = new[]
        {
            TargetStep(0, "A"),
            TargetStep(1, "B"),
            TargetStep(2, "C"),
            TargetStep(3, "D"),
            TargetStep(4, "E"),
        };
        StepStartTrigger TriggerFor(int idx) => idx switch
        {
            1 or 3 or 4 => StepStartTrigger.StartWithPrevious,
            _           => StepStartTrigger.StartAfterPrevious,
        };

        var waves = WavePartitioner.Partition(steps, TriggerFor);

        waves.Should().HaveCount(2);
        waves[0].Steps.Select(s => s.Name).Should().Equal(["A", "B"]);
        waves[1].Steps.Select(s => s.Name).Should().Equal(["C", "D", "E"]);
    }

    [Fact]
    public void First_step_StartWithPrevious_is_ignored()
    {
        // Sanity: the very first step has no predecessor, so its
        // StartWithPrevious is meaningless — it opens its own wave.
        var s0 = TargetStep(index: 0, name: "A");
        var s1 = TargetStep(index: 1, name: "B");

        var waves = WavePartitioner.Partition(
            steps: [s0, s1],
            triggerByIndex: _ => StepStartTrigger.StartWithPrevious);

        waves.Should().HaveCount(1, "even though step 0 is asked to start " +
            "with previous, there is no previous; the wave just absorbs step 1");
        waves[0].Steps.Should().HaveCount(2);
    }

    [Fact]
    public void Server_only_wave_is_classified_Server()
    {
        var s0 = ServerStep(index: 0, name: "Prep");
        var s1 = ServerStep(index: 1, name: "Pack");

        var waves = WavePartitioner.Partition(
            steps: [s0, s1],
            triggerByIndex: idx => idx == 1
                ? StepStartTrigger.StartWithPrevious
                : StepStartTrigger.StartAfterPrevious);

        waves.Should().HaveCount(1);
        waves[0].Kind.Should().Be(WavePartitioner.WaveKind.Server);
    }

    [Fact]
    public void DeployRelease_StepType_is_classified_Server_even_without_RunOnServer_flag()
    {
        // The orchestrator step types (Octopus.DeployRelease) are
        // intrinsically server-side even without the RunOnServer marker.
        // Verified via the mixed-wave gate: pairing a bare DeployRelease
        // with a target script step inside one wave must be refused as
        // mixed.
        var deployRelease = new DeploymentStepPlan(
            Index:          0,
            Name:           "Cascade",
            StepType:       DeployReleaseStepRunner.StepType,
            PackageId:      "",
            PackageVersion: "",
            Config:         new Dictionary<string, string>());
        var script = TargetStep(index: 1, name: "Deploy");

        var act = () => WavePartitioner.Partition(
            steps: [deployRelease, script],
            triggerByIndex: idx => idx == 1
                ? StepStartTrigger.StartWithPrevious
                : StepStartTrigger.StartAfterPrevious);

        var ex = act.Should().Throw<WavePartitioner.InvalidWaveException>().Which;
        ex.ServerStepNames.Should().Equal(["Cascade"]);
        ex.TargetStepNames.Should().Equal(["Deploy"]);
    }

    [Fact]
    public void Mixed_wave_throws_with_step_name_attribution()
    {
        // Two steps in the same wave, one server, one target → reject.
        var server = ServerStep(index: 0, name: "Notify");
        var target = TargetStep(index: 1, name: "Deploy");

        var act = () => WavePartitioner.Partition(
            steps: [server, target],
            triggerByIndex: idx => idx == 1
                ? StepStartTrigger.StartWithPrevious
                : StepStartTrigger.StartAfterPrevious);

        var ex = act.Should().Throw<WavePartitioner.InvalidWaveException>().Which;
        ex.ServerStepNames.Should().Equal(["Notify"]);
        ex.TargetStepNames.Should().Equal(["Deploy"]);
        ex.WaveSteps.Should().HaveCount(2);
        ex.Message.Should().Contain("Notify").And.Contain("Deploy");
    }

    [Fact]
    public void Mixed_wave_does_not_reject_a_clean_sequential_mix()
    {
        // [server], [target] sequentially (each its own wave because both
        // are StartAfterPrevious by default) — neither wave is mixed; the
        // partitioner accepts this.
        var server = ServerStep(index: 0, name: "Notify");
        var target = TargetStep(index: 1, name: "Deploy");

        var waves = WavePartitioner.Partition(
            steps: [server, target],
            triggerByIndex: _ => StepStartTrigger.StartAfterPrevious);

        waves.Should().HaveCount(2);
        waves[0].Kind.Should().Be(WavePartitioner.WaveKind.Server);
        waves[1].Kind.Should().Be(WavePartitioner.WaveKind.Target);
    }

    [Fact]
    public void Out_of_order_input_is_sorted_by_Index_internally()
    {
        // Caller hands the partitioner steps in some other order — the
        // partitioner sorts by Index so the wave's first step is the
        // SortOrder-0 step, not the input-array-0 step.
        var s2 = TargetStep(index: 2, name: "C");
        var s0 = TargetStep(index: 0, name: "A");
        var s1 = TargetStep(index: 1, name: "B");

        var waves = WavePartitioner.Partition(
            steps: [s2, s0, s1],
            triggerByIndex: _ => StepStartTrigger.StartAfterPrevious);

        waves.Select(w => w.Steps[0].Name).Should().Equal(["A", "B", "C"]);
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static DeploymentStepPlan ServerStep(int index, string name) =>
        new(Index:          index,
            Name:           name,
            StepType:       "Kraken.Script",
            PackageId:      "",
            PackageVersion: "",
            Config:         new Dictionary<string, string>
            {
                ["Octopus.Action.RunOnServer"] = "true",
            });

    private static DeploymentStepPlan TargetStep(int index, string name) =>
        new(Index:          index,
            Name:           name,
            StepType:       "Kraken.Script",
            PackageId:      "",
            PackageVersion: "",
            Config:         new Dictionary<string, string>());
}

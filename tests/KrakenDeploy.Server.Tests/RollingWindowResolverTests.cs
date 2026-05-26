using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// M-RollingDeployments Phase 2 — pins the rolling-window resolver's
/// ancestor walk + cap parsing + per-wave shared-ancestor rule. The
/// resolver intentionally returns null for malformed / mixed-ancestor
/// cases (operators don't get an accidental "one-target-at-a-time" cap).
/// </summary>
public sealed class RollingWindowResolverTests
{
    [Fact]
    public void Returns_null_when_no_ancestor_has_MaxParallelism()
    {
        // A plain top-level step: no parent, no cap.
        var leafSnap = MakeLeaf("Deploy");
        var snapById = new Dictionary<Guid, StepSnapshot> { [leafSnap.Id] = leafSnap };
        var wave = new[] { MakePlan(0, "Deploy") };
        var snapByIdx = new[] { leafSnap };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().BeNull();
    }

    [Fact]
    public void Resolves_cap_from_direct_parent_step_group()
    {
        var group = MakeGroup("RollingGroup", maxParallelism: "2");
        var child = MakeLeaf("Deploy", parentId: group.Id);
        var snapById = ToDict(group, child);
        var wave = new[] { MakePlan(0, "Deploy") };
        var snapByIdx = new[] { child };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().Be(2);

        RollingWindowResolver
            .ResolveWaveRollingGroupName(wave, snapByIdx, snapById)
            .Should().Be("RollingGroup");
    }

    [Fact]
    public void Walks_through_nested_non_rolling_groups()
    {
        var outerRolling = MakeGroup("Outer", maxParallelism: "3");
        var innerPlain   = MakeGroup("Inner", maxParallelism: null,
                                     parentId: outerRolling.Id);
        var child        = MakeLeaf("Deploy", parentId: innerPlain.Id);
        var snapById = ToDict(outerRolling, innerPlain, child);
        var wave = new[] { MakePlan(0, "Deploy") };
        var snapByIdx = new[] { child };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().Be(3,
                because: "the nearest non-rolling group is skipped; the next ancestor with a cap wins");
    }

    [Fact]
    public void Nearest_rolling_ancestor_wins_for_nested_rolling_groups()
    {
        // Defensive case — operators shouldn't author this, but if they do,
        // the inner (more-restrictive scope) wins.
        var outerRolling = MakeGroup("Outer", maxParallelism: "10");
        var innerRolling = MakeGroup("Inner", maxParallelism: "2",
                                     parentId: outerRolling.Id);
        var child        = MakeLeaf("Deploy", parentId: innerRolling.Id);
        var snapById = ToDict(outerRolling, innerRolling, child);
        var wave = new[] { MakePlan(0, "Deploy") };
        var snapByIdx = new[] { child };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().Be(2);
    }

    [Fact]
    public void Returns_null_when_cap_is_unparseable()
    {
        // Garbage in the cap value falls back to "no cap" rather than
        // accidentally serialising the deployment to one-target-at-a-time.
        var group = MakeGroup("RollingGroup", maxParallelism: "many");
        var child = MakeLeaf("Deploy", parentId: group.Id);
        var snapById = ToDict(group, child);
        var wave = new[] { MakePlan(0, "Deploy") };
        var snapByIdx = new[] { child };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_cap_is_zero_or_negative()
    {
        // 0 / -1 are nonsensical; same defensive fallback.
        var group = MakeGroup("RollingGroup", maxParallelism: "0");
        var child = MakeLeaf("Deploy", parentId: group.Id);
        var snapById = ToDict(group, child);
        var wave = new[] { MakePlan(0, "Deploy") };
        var snapByIdx = new[] { child };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_wave_steps_share_no_common_rolling_ancestor()
    {
        // Two steps in one wave, each under a different rolling group.
        // No batching applies — the resolver bails to avoid surprising the
        // operator with a partial cap.
        var groupA = MakeGroup("RegionA", maxParallelism: "2");
        var groupB = MakeGroup("RegionB", maxParallelism: "3");
        var leafA  = MakeLeaf("DeployA", parentId: groupA.Id);
        var leafB  = MakeLeaf("DeployB", parentId: groupB.Id);
        var snapById = ToDict(groupA, groupB, leafA, leafB);
        var wave = new[]
        {
            MakePlan(0, "DeployA"),
            MakePlan(1, "DeployB"),
        };
        var snapByIdx = new[] { leafA, leafB };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_only_some_wave_steps_have_a_rolling_ancestor()
    {
        // One step in a rolling group, one top-level. Same conservative fallback.
        var group = MakeGroup("RollingGroup", maxParallelism: "2");
        var child = MakeLeaf("DeployA", parentId: group.Id);
        var loose = MakeLeaf("DeployB");
        var snapById = ToDict(group, child, loose);
        var wave = new[]
        {
            MakePlan(0, "DeployA"),
            MakePlan(1, "DeployB"),
        };
        var snapByIdx = new[] { child, loose };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().BeNull();
    }

    [Fact]
    public void Resolves_cap_when_all_wave_steps_share_one_rolling_ancestor()
    {
        // Three children of the same rolling group emitted as one wave
        // (StartWithPrevious). Cap applies.
        var group = MakeGroup("RollingGroup", maxParallelism: "2");
        var c1 = MakeLeaf("A", parentId: group.Id);
        var c2 = MakeLeaf("B", parentId: group.Id);
        var c3 = MakeLeaf("C", parentId: group.Id);
        var snapById = ToDict(group, c1, c2, c3);
        var wave = new[]
        {
            MakePlan(0, "A"),
            MakePlan(1, "B"),
            MakePlan(2, "C"),
        };
        var snapByIdx = new[] { c1, c2, c3 };

        RollingWindowResolver
            .ResolveWaveMaxParallelism(wave, snapByIdx, snapById)
            .Should().Be(2);
    }

    [Fact]
    public void Chunk_returns_single_batch_when_cap_is_zero_or_negative()
    {
        var targets = new[] { "t1", "t2", "t3" };
        RollingWindowResolver.Chunk(targets, 0).Should().HaveCount(1);
        RollingWindowResolver.Chunk(targets, -1).Should().HaveCount(1);
        RollingWindowResolver.Chunk(targets, 0)[0]
            .Should().Equal("t1", "t2", "t3");
    }

    [Fact]
    public void Chunk_returns_single_batch_when_cap_meets_or_exceeds_count()
    {
        var targets = new[] { "t1", "t2", "t3" };
        RollingWindowResolver.Chunk(targets, 3)
            .Should().HaveCount(1).And.Subject.First().Should().Equal("t1", "t2", "t3");
        RollingWindowResolver.Chunk(targets, 99)
            .Should().HaveCount(1).And.Subject.First().Should().Equal("t1", "t2", "t3");
    }

    [Fact]
    public void Chunk_splits_into_contiguous_batches_in_declared_order()
    {
        var targets = new[] { "t1", "t2", "t3", "t4", "t5" };
        var batches = RollingWindowResolver.Chunk(targets, 2);
        batches.Should().HaveCount(3);
        batches[0].Should().Equal("t1", "t2");
        batches[1].Should().Equal("t3", "t4");
        batches[2].Should().ContainSingle(
            because: "the final batch carries the remainder when the total isn't evenly divisible")
            .Which.Should().Be("t5");
    }

    [Fact]
    public void Chunk_empty_input_returns_empty()
    {
        RollingWindowResolver.Chunk(Array.Empty<string>(), 5).Should().BeEmpty();
    }

    // ── Fixtures ────────────────────────────────────────────────────────

    private static StepSnapshot MakeGroup(
        string name, string? maxParallelism, Guid? parentId = null)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (maxParallelism is not null)
        {
            config[RollingWindowResolver.MaxParallelismKey] = maxParallelism;
        }
        return new StepSnapshot
        {
            Id            = Guid.NewGuid(),
            Name          = name,
            StepType      = KrakenStepTypes.StepGroup,
            Config        = config,
            ParentStepId  = parentId,
        };
    }

    private static StepSnapshot MakeLeaf(string name, Guid? parentId = null)
        => new()
        {
            Id            = Guid.NewGuid(),
            Name          = name,
            StepType      = "Octopus.Script",
            Config        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ParentStepId  = parentId,
        };

    private static DeploymentStepPlan MakePlan(int index, string name)
        => new(
            Index:          index,
            Name:           name,
            StepType:       "Octopus.Script",
            PackageId:      "",
            PackageVersion: "",
            Config:         new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static Dictionary<Guid, StepSnapshot> ToDict(params StepSnapshot[] snaps)
        => snaps.ToDictionary(s => s.Id);
}

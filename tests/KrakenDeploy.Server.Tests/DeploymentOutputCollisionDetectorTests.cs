using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M14.4 <see cref="DeploymentOutputCollisionDetector"/>.
/// Pin the last-writer-wins-by-SortOrder contract + case-insensitive name
/// matching + the empty / single-writer no-op paths.
/// </summary>
public sealed class DeploymentOutputCollisionDetectorTests
{
    [Fact]
    public void No_buckets_yields_no_collisions()
    {
        var collisions = DeploymentOutputCollisionDetector.Detect([]);
        collisions.Should().BeEmpty();
    }

    [Fact]
    public void Disjoint_variable_names_yield_no_collisions()
    {
        // Two siblings, neither writes a variable name the other writes.
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("A", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["foo"] = "1" }),
            ("B", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["bar"] = "2" }),
        });

        collisions.Should().BeEmpty();
    }

    [Fact]
    public void Single_writer_per_name_yields_no_collisions()
    {
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("A", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["x"] = "1" }),
        });

        collisions.Should().BeEmpty();
    }

    [Fact]
    public void Two_siblings_writing_same_name_collide_with_last_writer_winning()
    {
        // Caller passes buckets in SortOrder; the detector treats the last
        // bucket as the winner.
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("StepA", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["build"] = "first" }),
            ("StepB", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["build"] = "second" }),
        });

        collisions.Should().HaveCount(1);
        var c = collisions[0];
        c.VariableName.Should().Be("build");
        c.Writers.Should().HaveCount(2);
        c.Winner.StepName.Should().Be("StepB");
        c.Winner.Value.Should().Be("second");
        c.Losers.Should().ContainSingle()
            .Which.StepName.Should().Be("StepA");
    }

    [Fact]
    public void Three_siblings_collide_with_SortOrder_last_winning()
    {
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("A", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["foo"] = "a" }),
            ("B", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["foo"] = "b" }),
            ("C", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["foo"] = "c" }),
        });

        collisions.Should().HaveCount(1);
        var c = collisions[0];
        c.Winner.StepName.Should().Be("C");
        c.Losers.Select(w => w.StepName).Should().Equal(["A", "B"]);
    }

    [Fact]
    public void Variable_name_match_is_case_insensitive()
    {
        // Octostache and the rest of the engine treat variable names
        // case-insensitively. The detector must agree so "Foo" set by A
        // and "FOO" set by B count as the same name.
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("A", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Foo"] = "1" }),
            ("B", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["FOO"] = "2" }),
        });

        collisions.Should().HaveCount(1);
        collisions[0].Writers.Should().HaveCount(2);
    }

    [Fact]
    public void Multiple_distinct_collisions_are_each_reported()
    {
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("A", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string>
                {
                    ["x"] = "ax",
                    ["y"] = "ay",
                }),
            ("B", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string>
                {
                    ["x"] = "bx",
                    ["y"] = "by",
                    ["z"] = "bz", // not a collision — only one writer
                }),
        });

        collisions.Should().HaveCount(2);
        collisions.Select(c => c.VariableName).Should()
            .BeEquivalentTo(["x", "y"]);
    }

    [Fact]
    public void Empty_bucket_for_a_step_is_skipped()
    {
        // A step with no captured outputs (the common case) does not
        // contribute to the writer set for any variable.
        var collisions = DeploymentOutputCollisionDetector.Detect(new[]
        {
            ("A", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string> { ["foo"] = "1" }),
            ("B", (IReadOnlyDictionary<string, string>)
                new Dictionary<string, string>()),
        });

        collisions.Should().BeEmpty();
    }
}

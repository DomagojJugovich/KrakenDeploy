using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Transport;
using Octostache;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the M15.2 <see cref="DeploymentPlanFlattener"/>. Pin
/// every documented mode: pre-M15 flat process pass-through, Step Group
/// expansion (plain container), ForEach over array variables, empty
/// collection, undefined collection, iteration variable injection,
/// synthetic naming (clean + fallback), nested ForEach (innermost
/// shadowing), lazy collection resolution, parallel ForEach.
/// </summary>
public sealed class DeploymentPlanFlattenerTests
{
    private static readonly IReadOnlyDictionary<string, string[]> NoArrays
        = new Dictionary<string, string[]>();

    [Fact]
    public void Empty_snapshot_yields_empty_plans()
    {
        var result = DeploymentPlanFlattener.Flatten([], NoArrays, new VariableDictionary());

        result.Plans.Should().BeEmpty();
        result.SnapshotByPlanIndex.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Flat_process_passes_through_unchanged()
    {
        // Pre-M15 process (no Step Groups, no ForEach) flattens 1:1.
        var snap = new[]
        {
            NewLeaf("Prep",   sortOrder: 0),
            NewLeaf("Deploy", sortOrder: 1),
            NewLeaf("Verify", sortOrder: 2),
        };

        var result = DeploymentPlanFlattener.Flatten(snap, NoArrays, new VariableDictionary());

        result.Plans.Should().HaveCount(3);
        result.Plans.Select(p => p.Name).Should().Equal(["Prep", "Deploy", "Verify"]);
        result.Plans.Select(p => p.Index).Should().Equal([0, 1, 2]);
        result.Warnings.Should().BeEmpty();
        // SnapshotByPlanIndex maps each plan back to its snapshot.
        result.SnapshotByPlanIndex.Select(s => s.Name).Should().Equal(["Prep", "Deploy", "Verify"]);
    }

    [Fact]
    public void Step_Group_with_two_children_expands_to_two_plans_in_SortOrder()
    {
        // Group "Deploy" with children "A" then "B". The group emits no
        // plan of its own; children land in declared order.
        var group = NewGroup("Deploy", sortOrder: 0);
        var a = NewLeaf("A", sortOrder: 0, parent: group);
        var b = NewLeaf("B", sortOrder: 1, parent: group);

        var result = DeploymentPlanFlattener.Flatten(
            [group, a, b], NoArrays, new VariableDictionary());

        result.Plans.Should().HaveCount(2);
        result.Plans.Select(p => p.Name).Should().Equal(["A", "B"]);
    }

    [Fact]
    public void ForEach_over_three_item_array_emits_three_iterations()
    {
        // Group "Deploy" with one child "App" that loops over the
        // "envs" array variable [staging, qa, prod].
        var group = NewForEach("Deploy", "envs", sortOrder: 0);
        var app = NewLeaf("App", sortOrder: 0, parent: group);

        var result = DeploymentPlanFlattener.Flatten(
            [group, app],
            new Dictionary<string, string[]> { ["envs"] = ["staging", "qa", "prod"] },
            new VariableDictionary());

        result.Plans.Should().HaveCount(3);
        result.Plans.Select(p => p.Name).Should().Equal(
            ["App [item=staging]", "App [item=qa]", "App [item=prod]"]);
        result.Plans.Select(p => p.AccumulatorKey).Should().Equal(
            ["App[0]", "App[1]", "App[2]"]);
    }

    [Fact]
    public void Empty_collection_emits_no_plans_and_an_empty_warning()
    {
        var group = NewForEach("Deploy", "envs", sortOrder: 0);
        var app = NewLeaf("App", sortOrder: 0, parent: group);

        var result = DeploymentPlanFlattener.Flatten(
            [group, app],
            new Dictionary<string, string[]> { ["envs"] = [] },
            new VariableDictionary());

        result.Plans.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(w =>
            w.Kind == DeploymentPlanFlattener.WarningKind.ForEachEmpty
            && w.Source.Name == "Deploy");
    }

    [Fact]
    public void Undefined_collection_emits_no_plans_and_an_unresolved_warning()
    {
        // The orchestrator (not the flattener) decides whether to abort
        // based on the group's Required flag. The flattener just surfaces
        // the warning.
        var group = NewForEach("Deploy", "missing", sortOrder: 0);
        var app = NewLeaf("App", sortOrder: 0, parent: group);

        var result = DeploymentPlanFlattener.Flatten(
            [group, app], NoArrays, new VariableDictionary());

        result.Plans.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(w =>
            w.Kind == DeploymentPlanFlattener.WarningKind.ForEachUnresolved
            && w.Source.Name == "Deploy");
    }

    [Fact]
    public void Iteration_variable_substitutes_into_child_Config()
    {
        // The flattener applies Octostache substitution per emitted plan,
        // so #{item} in a child's ScriptBody resolves to that iteration's
        // value (not "always the last item").
        var group = NewForEach("Deploy", "envs", sortOrder: 0);
        var app = NewLeaf("App", sortOrder: 0, parent: group);
        app.Config["Octopus.Action.Script.ScriptBody"] = "echo #{item}";

        var result = DeploymentPlanFlattener.Flatten(
            [group, app],
            new Dictionary<string, string[]> { ["envs"] = ["staging", "prod"] },
            new VariableDictionary());

        result.Plans[0].Config["Octopus.Action.Script.ScriptBody"]
            .Should().Be("echo staging");
        result.Plans[1].Config["Octopus.Action.Script.ScriptBody"]
            .Should().Be("echo prod");
    }

    [Fact]
    public void Index_variable_substitutes_with_zero_based_position()
    {
        var group = NewForEach("Loop", "items", sortOrder: 0);
        var leaf = NewLeaf("Step", sortOrder: 0, parent: group);
        leaf.Config["Octopus.Action.Script.ScriptBody"] = "iteration #{index} of #{item}";

        var result = DeploymentPlanFlattener.Flatten(
            [group, leaf],
            new Dictionary<string, string[]> { ["items"] = ["a", "b"] },
            new VariableDictionary());

        result.Plans[0].Config["Octopus.Action.Script.ScriptBody"]
            .Should().Be("iteration 0 of a");
        result.Plans[1].Config["Octopus.Action.Script.ScriptBody"]
            .Should().Be("iteration 1 of b");
    }

    [Fact]
    public void Long_iteration_value_falls_back_to_index_display_name()
    {
        // Display name "OriginalName [var=value]" only when value is
        // "clean" (≤ 40 chars, no newlines/tabs/']'); otherwise fallback
        // to "OriginalName [var=#index]".
        var group = NewForEach("Deploy", "blobs", sortOrder: 0);
        var leaf = NewLeaf("App", sortOrder: 0, parent: group);

        var longValue = new string('x', 100);
        var result = DeploymentPlanFlattener.Flatten(
            [group, leaf],
            new Dictionary<string, string[]> { ["blobs"] = [longValue, "short"] },
            new VariableDictionary());

        result.Plans[0].Name.Should().Be("App [item=#0]");
        result.Plans[1].Name.Should().Be("App [item=short]");
        // AccumulatorKey is always the stable synthetic form regardless
        // of display.
        result.Plans[0].AccumulatorKey.Should().Be("App[0]");
        result.Plans[1].AccumulatorKey.Should().Be("App[1]");
    }

    [Fact]
    public void Nested_ForEach_resolves_inner_variable_shadowing_outer()
    {
        // outer ForEach env in [staging, prod]
        //   inner ForEach env2 in [...]  // (artificial nesting)
        // Inner #{item} shadows the outer's. The outer's variable name
        // stays accessible if it used a distinct IterationVariable.
        var outer = NewForEach("Outer", "envs", sortOrder: 0);
        var inner = NewForEach("Inner", "instances", sortOrder: 0, parent: outer);
        var leaf = NewLeaf("Run", sortOrder: 0, parent: inner);
        leaf.Config["Octopus.Action.Script.ScriptBody"] = "deploy #{item}";

        var result = DeploymentPlanFlattener.Flatten(
            [outer, inner, leaf],
            new Dictionary<string, string[]>
            {
                ["envs"]      = ["staging", "prod"],
                ["instances"] = ["i1", "i2"],
            },
            new VariableDictionary());

        // 2 outer × 2 inner = 4 emissions.
        result.Plans.Should().HaveCount(4);
        result.Plans.Select(p => p.Config["Octopus.Action.Script.ScriptBody"])
            .Should().Equal(["deploy i1", "deploy i2", "deploy i1", "deploy i2"],
                "innermost #{item} shadows outer #{item}");
    }

    [Fact]
    public void Inner_ForEach_collection_resolves_lazily_per_outer_iteration()
    {
        // Inner ForEach's Collection references the outer's iteration
        // variable (#{env}). Resolution happens per outer iteration so the
        // inner sees the right collection each time.
        var outer = NewForEach("Outer", "envs", sortOrder: 0,
            iterationVariable: "env");
        var inner = NewForEach("Inner", "#{env}-instances", sortOrder: 0, parent: outer);
        var leaf = NewLeaf("Run", sortOrder: 0, parent: inner);
        leaf.Config["Octopus.Action.Script.ScriptBody"] = "deploy #{item} on #{env}";

        var result = DeploymentPlanFlattener.Flatten(
            [outer, inner, leaf],
            new Dictionary<string, string[]>
            {
                ["envs"]              = ["staging", "prod"],
                ["staging-instances"] = ["s1"],
                ["prod-instances"]    = ["p1", "p2"],
            },
            new VariableDictionary());

        // staging has 1 instance + prod has 2 = 3 emissions total.
        result.Plans.Should().HaveCount(3);
        result.Plans.Select(p => p.Config["Octopus.Action.Script.ScriptBody"])
            .Should().Equal([
                "deploy s1 on staging",
                "deploy p1 on prod",
                "deploy p2 on prod",
            ]);
    }

    [Fact]
    public void Parallel_ForEach_emits_iterations_with_StartWithPrevious()
    {
        // Octopus.Action.ForEach.Parallel = "true" makes iterations
        // siblings in a wave — iteration 0 opens the wave; iterations 1..N
        // join via StartWithPrevious.
        var group = NewForEach("Deploy", "envs", sortOrder: 0);
        group.Config["Octopus.Action.ForEach.Parallel"] = "true";
        var leaf = NewLeaf("App", sortOrder: 0, parent: group);

        var result = DeploymentPlanFlattener.Flatten(
            [group, leaf],
            new Dictionary<string, string[]> { ["envs"] = ["a", "b", "c"] },
            new VariableDictionary());

        result.Plans.Should().HaveCount(3);
        result.Plans[0].StartTrigger.Should().Be(
            (int)StepStartTrigger.StartAfterPrevious,
            "iteration 0 opens the wave");
        result.Plans[1].StartTrigger.Should().Be(
            (int)StepStartTrigger.StartWithPrevious,
            "iterations 1..N join the same wave");
        result.Plans[2].StartTrigger.Should().Be(
            (int)StepStartTrigger.StartWithPrevious);
    }

    [Fact]
    public void Sequential_ForEach_iterations_run_StartAfterPrevious()
    {
        // Default mode (no Parallel flag): iteration N waits for iteration
        // N-1 to finish.
        var group = NewForEach("Deploy", "envs", sortOrder: 0);
        var leaf = NewLeaf("App", sortOrder: 0, parent: group);

        var result = DeploymentPlanFlattener.Flatten(
            [group, leaf],
            new Dictionary<string, string[]> { ["envs"] = ["a", "b"] },
            new VariableDictionary());

        result.Plans[0].StartTrigger.Should().Be((int)StepStartTrigger.StartAfterPrevious);
        result.Plans[1].StartTrigger.Should().Be((int)StepStartTrigger.StartAfterPrevious);
    }

    [Fact]
    public void Pre_M15_snapshot_with_Guid_Empty_Ids_walks_as_flat_list()
    {
        // Snapshots cut before M15.1 don't have the Id field; they
        // deserialise with Id = Guid.Empty. The flattener treats them as
        // top-level (no children resolve through Guid.Empty parent lookup),
        // matching the pre-M15 runtime.
        var legacy = new StepSnapshot
        {
            // Id intentionally not set → Guid.Empty.
            Name      = "LegacyStep",
            StepType  = "Kraken.Script",
            PackageId = "",
            SortOrder = 0,
            Config    = [],
        };

        var result = DeploymentPlanFlattener.Flatten(
            [legacy], NoArrays, new VariableDictionary());

        result.Plans.Should().HaveCount(1);
        result.Plans[0].Name.Should().Be("LegacyStep");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static StepSnapshot NewLeaf(
        string name, int sortOrder, StepSnapshot? parent = null)
        => new()
        {
            Id           = Guid.CreateVersion7(),
            ParentStepId = parent?.Id,
            Name         = name,
            StepType     = "Kraken.Script",
            PackageId    = "",
            SortOrder    = sortOrder,
            Config       = [],
        };

    private static StepSnapshot NewGroup(
        string name, int sortOrder, StepSnapshot? parent = null)
        => new()
        {
            Id           = Guid.CreateVersion7(),
            ParentStepId = parent?.Id,
            Name         = name,
            StepType     = KrakenStepTypes.StepGroup,
            PackageId    = "",
            SortOrder    = sortOrder,
            Config       = [],
        };

    private static StepSnapshot NewForEach(
        string name,
        string collection,
        int sortOrder,
        string? iterationVariable = null,
        StepSnapshot? parent = null)
    {
        var snap = NewGroup(name, sortOrder, parent);
        snap.Config["Octopus.Action.ForEach.Collection"] = collection;
        if (iterationVariable is not null)
        {
            snap.Config["Octopus.Action.ForEach.IterationVariable"] = iterationVariable;
        }
        return snap;
    }
}

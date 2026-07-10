using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Runbooks;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Unit tests for the M15 <see cref="ProcessValidator"/>. Pin every
/// structural invariant the flattener + orchestrator rely on, plus
/// the happy paths (flat process, tree of one Step Group with two
/// children). The validator runs as defence in depth at the flattener
/// too, so cycle / unknown-parent must be rejected with a clear error.
/// </summary>
public sealed class ProcessValidatorTests
{
    [Fact]
    public void Empty_list_is_valid()
    {
        var result = ProcessValidator.Validate([]);
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Flat_single_step_is_valid()
    {
        var step = NewLeaf("Deploy");
        var result = ProcessValidator.Validate([step]);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Leaf_type_with_children_is_rejected()
    {
        // A Kraken.Script step cannot have children — only Kraken.StepGroup can.
        var parent = NewLeaf("Run script", stepType: "Kraken.Script");
        var child  = NewLeaf("Inner", parentId: parent.Id);

        var result = ProcessValidator.Validate([parent, child]);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.Code == ProcessValidator.ValidationErrorCode.LeafTypeHasChildren
            && e.StepId == parent.Id);
    }

    [Fact]
    public void StepGroup_with_leaf_only_config_key_is_rejected()
    {
        // Step Groups must not carry leaf semantics (script body, package
        // selectors, etc.).
        var group = NewLeaf("Bad group", stepType: KrakenStepTypes.StepGroup);
        group.Config["Octopus.Action.Script.ScriptBody"] = "Write-Host 'oops'";

        var result = ProcessValidator.Validate([group]);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.Code == ProcessValidator.ValidationErrorCode.GroupHasLeafConfig
            && e.StepId == group.Id);
        result.Errors[0].Message.Should().Contain("Octopus.Action.Script.ScriptBody");
    }

    [Fact]
    public void ParentStepId_pointing_to_unknown_step_is_rejected()
    {
        var orphan = NewLeaf("Orphan child", parentId: Guid.NewGuid());

        var result = ProcessValidator.Validate([orphan]);

        result.Errors.Should().ContainSingle(e =>
            e.Code == ProcessValidator.ValidationErrorCode.UnknownParent
            && e.StepId == orphan.Id);
    }

    [Fact]
    public void Self_cycle_is_rejected()
    {
        var step = NewLeaf("Self-parent");
        step.ParentStepId = step.Id;

        var result = ProcessValidator.Validate([step]);

        result.Errors.Should().Contain(e =>
            e.Code == ProcessValidator.ValidationErrorCode.Cycle);
    }

    [Fact]
    public void Two_step_cycle_is_rejected()
    {
        // A → B → A. Both steps end up part of the same cycle; the
        // validator catches at least one (it doesn't have to catch
        // both since the operator only needs to break the loop once).
        var a = NewLeaf("A", stepType: KrakenStepTypes.StepGroup);
        var b = NewLeaf("B", stepType: KrakenStepTypes.StepGroup);
        a.ParentStepId = b.Id;
        b.ParentStepId = a.Id;

        var result = ProcessValidator.Validate([a, b]);

        result.Errors.Should().Contain(e =>
            e.Code == ProcessValidator.ValidationErrorCode.Cycle);
    }

    [Fact]
    public void Happy_path_step_group_with_two_children_is_valid()
    {
        var group = NewLeaf("My group", stepType: KrakenStepTypes.StepGroup);
        var c1    = NewLeaf("Child 1", parentId: group.Id);
        var c2    = NewLeaf("Child 2", parentId: group.Id);

        var result = ProcessValidator.Validate([group, c1, c2]);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_accumulates_multiple_errors_in_one_pass()
    {
        // Editor expects every problem at once, not error-by-error.
        var leaf = NewLeaf("Leaf with kid", stepType: "Kraken.Script");
        var kid  = NewLeaf("Kid", parentId: leaf.Id);
        var orphan = NewLeaf("Orphan", parentId: Guid.NewGuid());

        var result = ProcessValidator.Validate([leaf, kid, orphan]);

        result.Errors.Should().HaveCount(2);
        result.Errors.Select(e => e.Code).Should().BeEquivalentTo(
            new[]
            {
                ProcessValidator.ValidationErrorCode.LeafTypeHasChildren,
                ProcessValidator.ValidationErrorCode.UnknownParent,
            });
    }

    [Fact]
    public void StepGroup_without_leaf_config_keys_is_valid()
    {
        // A bare Step Group with no children is currently odd but not
        // invalid — the editor can build one then add children later.
        // The validator MUST NOT reject it.
        var group = NewLeaf("Empty group", stepType: KrakenStepTypes.StepGroup);
        // ForEach properties are not in the leaf-only list — they're
        // valid on a Step Group.
        group.Config["Octopus.Action.ForEach.Collection"] = "envs";

        var result = ProcessValidator.Validate([group]);

        result.IsValid.Should().BeTrue();
    }

    // ── M15 follow-up: validator works for ProcessStep too ─────────────

    [Fact]
    public void Validator_works_for_ProcessStep_via_IComposableStep()
    {
        // The same validator + the same rules apply to runbook step
        // composition. Pin the cross-entity contract: ProcessStep
        // implements IComposableStep, so IEnumerable<ProcessStep>
        // passes covariantly to Validate.
        var group = NewRunbookLeaf("My runbook group",
            stepType: KrakenStepTypes.StepGroup);
        var c1 = NewRunbookLeaf("Child 1", parentId: group.Id);
        var c2 = NewRunbookLeaf("Child 2", parentId: group.Id);

        var result = ProcessValidator.Validate([group, c1, c2]);

        result.IsValid.Should().BeTrue(
            "the validator must accept the same happy-path tree on " +
            "ProcessStep that it accepts on ProcessStep");
    }

    [Fact]
    public void Validator_rejects_ProcessStep_leaf_with_children()
    {
        // The LeafTypeHasChildren rule applies symmetrically — a runbook
        // leaf step (Kraken.Script) cannot have child steps either.
        var parent = NewRunbookLeaf("Bad parent", stepType: "Kraken.Script");
        var child  = NewRunbookLeaf("Orphan child", parentId: parent.Id);

        var result = ProcessValidator.Validate([parent, child]);

        result.Errors.Should().ContainSingle(e =>
            e.Code == ProcessValidator.ValidationErrorCode.LeafTypeHasChildren
            && e.StepId == parent.Id);
    }

    private static ProcessStep NewRunbookLeaf(
        string name,
        string stepType = "Kraken.Script",
        Guid? parentId = null) => new()
    {
        Id           = Guid.CreateVersion7(),
        Name         = name,
        StepType     = stepType,
        PackageId    = "",
        ProcessId    = Guid.NewGuid(),
        ParentStepId = parentId,
        Config       = [],
        TargetRoles  = [],
    };

    // ── helper ─────────────────────────────────────────────────────────

    private static ProcessStep NewLeaf(
        string name,
        string stepType = "Kraken.Script",
        Guid? parentId = null) => new()
    {
        Id           = Guid.CreateVersion7(),
        Name         = name,
        StepType     = stepType,
        PackageId    = "",
        ProcessId    = Guid.NewGuid(),
        ParentStepId = parentId,
        Config       = [],
        TargetRoles  = [],
    };
}

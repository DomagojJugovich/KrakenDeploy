using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for the M15.1 importer — Octopus multi-action steps become
/// a <c>Kraken.StepGroup</c> parent + one child per action. Pin the
/// parent-child shape, the StartTrigger forcing on children 2..N
/// (Octopus's parallel-on-same-target default), step-level property
/// preservation (especially <c>Octopus.Action.MaxParallelism</c> for
/// the future M-RollingDeployments milestone), and the import-time
/// warning.
/// </summary>
public sealed class OctopusImporterM15MultiActionTests
{
    [Fact]
    public void Single_action_step_still_imports_as_a_flat_leaf()
    {
        // Regression: the pre-M15 import path must keep producing a
        // single flat ParsedStep for the common case.
        const string json = """
        {
          "Steps": [
            {
              "Name": "Deploy WebApp",
              "Properties": { "Octopus.Action.TargetRoles": "WEB" },
              "Actions": [
                {
                  "Name": "Deploy WebApp",
                  "ActionType": "Octopus.Script",
                  "Properties": { "Octopus.Action.Script.ScriptBody": "echo hi" }
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        result.Steps[0].StepType.Should().Be("Octopus.Script");
        result.Steps[0].Children.Should().BeNull(
            "single-action step imports as a flat leaf, not a Step Group");
    }

    [Fact]
    public void Multi_action_step_becomes_Kraken_StepGroup_parent_with_children()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "Deploy app + sidecar",
              "Properties": { "Octopus.Action.TargetRoles": "WEB" },
              "Actions": [
                {
                  "Name": "Deploy app",
                  "ActionType": "Octopus.Script",
                  "Properties": { "Octopus.Action.Script.ScriptBody": "deploy app" }
                },
                {
                  "Name": "Deploy sidecar",
                  "ActionType": "Octopus.Script",
                  "Properties": { "Octopus.Action.Script.ScriptBody": "deploy sidecar" }
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        var parent = result.Steps[0];
        parent.StepType.Should().Be(KrakenStepTypes.StepGroup);
        parent.Name.Should().Be("Deploy app + sidecar");
        parent.TargetRoles.Should().BeEquivalentTo(["WEB"]);

        parent.Children.Should().NotBeNull().And.HaveCount(2);
        parent.Children![0].Name.Should().Be("Deploy app");
        parent.Children![1].Name.Should().Be("Deploy sidecar");
        parent.Children![0].StepType.Should().Be("Octopus.Script");
    }

    [Fact]
    public void Children_2_through_N_have_StartTrigger_StartWithPrevious()
    {
        // Octopus's default for multi-action steps is parallel-on-same-
        // target. To preserve runtime semantics on import, children 2..N
        // get StartTrigger=StartWithPrevious. The first child opens the
        // wave with StartAfterPrevious (the default).
        const string json = """
        {
          "Steps": [
            {
              "Name": "Triple",
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} },
                { "Name": "B", "ActionType": "Octopus.Script", "Properties": {} },
                { "Name": "C", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        var children = result.Steps[0].Children!;
        children.Should().HaveCount(3);
        children[0].StartTrigger.Should().Be(StepStartTrigger.StartAfterPrevious);
        children[1].StartTrigger.Should().Be(StepStartTrigger.StartWithPrevious);
        children[2].StartTrigger.Should().Be(StepStartTrigger.StartWithPrevious);
    }

    [Fact]
    public void Parent_carries_step_level_MaxParallelism_for_M_RollingDeployments()
    {
        // Reserved for the future rolling-deployments milestone. The
        // importer reads + preserves Octopus.Action.MaxParallelism on the
        // parent's Config; nothing in M15 acts on it but the value
        // round-trips intact.
        const string json = """
        {
          "Steps": [
            {
              "Name": "Rolling deploy",
              "Properties": {
                "Octopus.Action.TargetRoles": "WEB",
                "Octopus.Action.MaxParallelism": "5"
              },
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} },
                { "Name": "B", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        var parent = result.Steps[0];
        parent.Config.Should().ContainKey("Octopus.Action.MaxParallelism");
        parent.Config["Octopus.Action.MaxParallelism"].Should().Be("5");
        // TargetRoles is surfaced on the ParsedStep, NOT duplicated in Config.
        parent.Config.Should().NotContainKey("Octopus.Action.TargetRoles");
    }

    [Fact]
    public void Multi_action_emits_import_time_warning_explaining_StartTrigger()
    {
        const string json = """
        {
          "Steps": [
            {
              "Name": "Multi",
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} },
                { "Name": "B", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Warnings.Should().Contain(w =>
            w.StepName == "Multi"
            && w.Message.Contains("Step Group")
            && w.Message.Contains("StartWithPrevious"));
    }

    [Fact]
    public void Children_inherit_TargetRoles_from_the_step_level_Properties()
    {
        // Octopus.Action.TargetRoles is a step-level field. The pre-M15
        // importer surfaced it on the (flat) step's TargetRoles; M15
        // surfaces it on the parent AND on every child so role-based
        // dispatch keeps working.
        const string json = """
        {
          "Steps": [
            {
              "Name": "Web fan-out",
              "Properties": { "Octopus.Action.TargetRoles": "WEB,API" },
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} },
                { "Name": "B", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        var parent = result.Steps[0];
        parent.TargetRoles.Should().BeEquivalentTo(["WEB", "API"]);
        parent.Children![0].TargetRoles.Should().BeEquivalentTo(["WEB", "API"]);
        parent.Children![1].TargetRoles.Should().BeEquivalentTo(["WEB", "API"]);
    }
}

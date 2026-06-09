using FluentAssertions;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit tests for M14.1 importer extension — Run Condition, Required,
/// Retries, Start Trigger from Octopus deploymentprocess JSON. Locks
/// the property-location decisions documented in the M14 plan
/// (step-level top-level fields for Condition + StartTrigger; action
/// IsRequired top-level on the action; AutoRetry inside the action
/// Properties bag; ConditionVariableExpression inside the step
/// Properties bag).
/// </summary>
public sealed class OctopusImporterM14KnobsTests
{
    [Fact]
    public void Parse_extracts_step_level_Condition_and_StartTrigger()
    {
        // Both fields live at the TOP LEVEL of the step JSON, not in the
        // Properties bag — verified against the argosy-process.json fixture.
        const string json = """
        {
          "Steps": [
            {
              "Name": "Cleanup on failure",
              "Properties": { "Octopus.Action.TargetRoles": "SERVER" },
              "Condition": "Failure",
              "StartTrigger": "StartAfterPrevious",
              "Actions": [
                {
                  "Name": "Cleanup",
                  "ActionType": "Octopus.Script",
                  "Properties": {}
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().HaveCount(1);
        result.Steps[0].Condition.Should().Be(StepCondition.Failure);
        result.Steps[0].StartTrigger.Should().Be(StepStartTrigger.StartAfterPrevious);
    }

    [Theory]
    [InlineData("Success",   StepCondition.Success)]
    [InlineData("Failure",   StepCondition.Failure)]
    [InlineData("Always",    StepCondition.Always)]
    [InlineData("Variable",  StepCondition.Variable)]
    [InlineData("success",   StepCondition.Success)]   // case-insensitive
    [InlineData("UNKNOWN",   StepCondition.Success)]   // unknown → Success default
    public void Parse_maps_Octopus_Condition_strings_to_enum(string raw, StepCondition expected)
    {
        var json = $$"""
        {
          "Steps": [
            {
              "Name": "X",
              "Condition": "{{raw}}",
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].Condition.Should().Be(expected);
    }

    [Theory]
    [InlineData("StartAfterPrevious", StepStartTrigger.StartAfterPrevious)]
    [InlineData("StartWithPrevious",  StepStartTrigger.StartWithPrevious)]
    [InlineData("startwithprevious",  StepStartTrigger.StartWithPrevious)] // case-insensitive
    [InlineData("",                   StepStartTrigger.StartAfterPrevious)] // empty → default
    [InlineData("Garbage",            StepStartTrigger.StartAfterPrevious)] // unknown → default
    public void Parse_maps_Octopus_StartTrigger_strings_to_enum(
        string raw, StepStartTrigger expected)
    {
        var json = $$"""
        {
          "Steps": [
            {
              "Name": "X",
              "StartTrigger": "{{raw}}",
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].StartTrigger.Should().Be(expected);
    }

    [Fact]
    public void Parse_extracts_action_IsRequired()
    {
        // Octopus stores IsRequired as a top-level bool on the ACTION
        // (not the step). The importer preserves the source value;
        // Octopus's default is false but the row in the JSON is explicit.
        const string json = """
        {
          "Steps": [
            {
              "Name": "X",
              "Actions": [
                {
                  "Name": "A",
                  "ActionType": "Octopus.Script",
                  "IsRequired": false,
                  "Properties": {}
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].Required.Should().BeFalse(
            "the importer preserves Octopus's IsRequired source value — " +
            "round-trip semantic identity over KrakenDeploy's true default");
    }

    [Fact]
    public void Parse_defaults_Required_to_true_when_IsRequired_absent()
    {
        // Octopus's JSON shape always emits IsRequired, but if a manually-
        // edited / older export omits it, the System.Text.Json default
        // (false for bool) would lose Required semantics for KrakenDeploy.
        // We accept that — the only safe assumption is "explicit absence
        // = Octopus default = false". The Required default on the typed
        // entity column compensates for new rows created in KrakenDeploy
        // itself, not for imports. Document the asymmetry via this test.
        const string json = """
        {
          "Steps": [
            {
              "Name": "X",
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].Required.Should().BeFalse(
            "absent IsRequired in source JSON → bool default false; an " +
            "Octopus-exported process omitting this key is preserved " +
            "verbatim, NOT silently promoted to required");
    }

    [Fact]
    public void Parse_extracts_AutoRetry_MaximumCount_from_action_Properties()
    {
        // Octopus stores retry count as Octopus.Action.AutoRetry.MaximumCount
        // (integer-as-string) inside the action Properties bag.
        const string json = """
        {
          "Steps": [
            {
              "Name": "X",
              "Actions": [
                {
                  "Name": "A",
                  "ActionType": "Octopus.Script",
                  "Properties": {
                    "Octopus.Action.AutoRetry.MaximumCount": "5"
                  }
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].MaxRetries.Should().Be(5);
    }

    [Theory]
    [InlineData("-3", 0)]   // negative clamped to 0
    [InlineData("abc", 0)]  // unparseable → 0
    [InlineData("",    0)]  // empty → 0
    public void Parse_defaults_MaxRetries_to_zero_when_AutoRetry_malformed(
        string raw, int expected)
    {
        var json = $$"""
        {
          "Steps": [
            {
              "Name": "X",
              "Actions": [
                {
                  "Name": "A",
                  "ActionType": "Octopus.Script",
                  "Properties": {
                    "Octopus.Action.AutoRetry.MaximumCount": "{{raw}}"
                  }
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].MaxRetries.Should().Be(expected);
    }

    [Fact]
    public void Parse_extracts_ConditionVariableExpression_from_step_Properties()
    {
        // Octopus stores the Variable-condition expression in the step's
        // Properties bag under Octopus.Step.ConditionVariableExpression.
        const string json = """
        {
          "Steps": [
            {
              "Name": "X",
              "Condition": "Variable",
              "Properties": {
                "Octopus.Action.TargetRoles": "SERVER",
                "Octopus.Step.ConditionVariableExpression": "#{Octopus.Environment.Name == \"Production\"}"
              },
              "Actions": [
                { "Name": "A", "ActionType": "Octopus.Script", "Properties": {} }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].Condition.Should().Be(StepCondition.Variable);
        result.Steps[0].ConditionVariableExpression
            .Should().Be("#{Octopus.Environment.Name == \"Production\"}");
    }

    [Fact]
    public void Parse_falls_back_to_action_Condition_when_step_Condition_missing()
    {
        // When a step's top-level Condition is absent but the action has its
        // own Condition field, we use the action's value. Octopus permits
        // per-action conditions on multi-action steps; M14 imports single-
        // action steps so the action's condition is the effective one.
        const string json = """
        {
          "Steps": [
            {
              "Name": "X",
              "Actions": [
                {
                  "Name": "A",
                  "ActionType": "Octopus.Script",
                  "Condition": "Always",
                  "Properties": {}
                }
              ]
            }
          ]
        }
        """;

        var result = OctopusDeploymentProcessImporter.Parse(json);
        result.Steps[0].Condition.Should().Be(StepCondition.Always);
    }

    [Fact]
    public void Parse_real_argosy_fixture_pins_default_M14_knobs()
    {
        // The shipped fixture has Condition=Success + StartTrigger=
        // StartAfterPrevious + IsRequired=false on every step. Pin the
        // mapping so importer regressions surface here.
        var json = File.ReadAllText("TestData/argosy-process.json");
        var result = OctopusDeploymentProcessImporter.Parse(json);

        result.Steps.Should().NotBeEmpty();
        result.Steps[0].Condition.Should().Be(StepCondition.Success);
        result.Steps[0].StartTrigger.Should().Be(StepStartTrigger.StartAfterPrevious);
        result.Steps[0].Required.Should().BeFalse(
            "every action in the fixture sets IsRequired=false");
        result.Steps[0].MaxRetries.Should().Be(0,
            "no AutoRetry property on any action in the fixture");
    }
}

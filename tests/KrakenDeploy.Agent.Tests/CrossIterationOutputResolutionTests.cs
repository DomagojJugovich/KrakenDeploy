using FluentAssertions;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Contracts;
using Octostache;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// M15.2 follow-up — pins the cross-iteration output reference contract
/// end-to-end through the Octostache resolution chain. The flattener
/// emits per-iteration plans with stable
/// <see cref="DeploymentStepPlan.AccumulatorKey"/> values
/// (<c>"Deploy[0]"</c>, <c>"Deploy[1]"</c>, …); the agent reports
/// outputs against that key; subsequent plans get those outputs
/// merged into <see cref="DeploymentPlan.Variables"/> as
/// <c>Octopus.Action[Deploy[0]].Output.X = value</c> entries via
/// <see cref="OutputVariableAccumulator.AugmentPlanWithPriorOutputs"/>.
/// Octostache then resolves
/// <c>#{Octopus.Action[Deploy[0]].Output.X}</c> against those entries.
///
/// <para>
/// These tests are the operator-visible contract: M15 docs (architecture.md
/// "Step composition") promise this works. The full pipeline (flattener
/// → agent → server → DB → Octostache) needs the worker harness gap M14
/// also hit; this file pins the agent + Octostache halves which are
/// directly testable today.
/// </para>
/// </summary>
public sealed class CrossIterationOutputResolutionTests
{
    [Fact]
    public void Synthetic_accumulator_key_resolves_via_Octostache_after_merge()
    {
        // Iteration 0 produces output "PackageVersion=1.2.3". The agent
        // reports it against the accumulator key "Deploy[0]" (M15.2
        // contract). The accumulator merges it into the next plan's
        // Variables dict as the canonical Octostache key.
        var basePlan = NewPlan();
        var outputs = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Deploy[0]"] = new() { ["PackageVersion"] = "1.2.3" },
        };

        var augmented = OutputVariableAccumulator.AugmentPlanWithPriorOutputs(
            basePlan, outputs);

        var vars = ToVariableDictionary(augmented.Variables);
        vars.Evaluate("#{Octopus.Action[Deploy[0]].Output.PackageVersion}")
            .Should().Be("1.2.3",
                "the synthetic-key form is the documented cross-iteration " +
                "reference; M15.2 architecture.md pins this");
    }

    [Fact]
    public void Multiple_iterations_resolve_to_their_own_values()
    {
        // Operators reference different iterations explicitly.
        // Deploy[0] → "staging"; Deploy[1] → "prod" — each picked
        // independently via the iteration index.
        var basePlan = NewPlan();
        var outputs = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Deploy[0]"] = new() { ["Url"] = "https://staging.example.com" },
            ["Deploy[1]"] = new() { ["Url"] = "https://prod.example.com" },
        };

        var augmented = OutputVariableAccumulator.AugmentPlanWithPriorOutputs(
            basePlan, outputs);
        var vars = ToVariableDictionary(augmented.Variables);

        vars.Evaluate("#{Octopus.Action[Deploy[0]].Output.Url}")
            .Should().Be("https://staging.example.com");
        vars.Evaluate("#{Octopus.Action[Deploy[1]].Output.Url}")
            .Should().Be("https://prod.example.com");
    }

    [Fact]
    public void Display_name_form_also_resolves_when_used_as_step_key()
    {
        // M15.2 architecture.md documents the synthetic-key form as
        // primary BUT notes the display-name form happens to work too
        // because the synthetic display name (e.g. "Deploy [item=staging]")
        // is itself a step name in the plan. Pin that contract — operators
        // who type the long form in scripts should get the right value.
        var basePlan = NewPlan();
        var outputs = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Deploy [item=staging]"] = new() { ["Url"] = "https://staging.example.com" },
        };

        var augmented = OutputVariableAccumulator.AugmentPlanWithPriorOutputs(
            basePlan, outputs);
        var vars = ToVariableDictionary(augmented.Variables);

        vars.Evaluate("#{Octopus.Action[Deploy [item=staging]].Output.Url}")
            .Should().Be("https://staging.example.com");
    }

    [Fact]
    public void Augment_with_no_prior_outputs_returns_the_base_plan_unchanged()
    {
        // First wave / first step: outputsByStep is empty. The accumulator
        // returns the same plan instance (cheap) and the Variables dict
        // is untouched.
        var basePlan = NewPlan(("ExistingVar", "preserved"));
        var augmented = OutputVariableAccumulator.AugmentPlanWithPriorOutputs(
            basePlan,
            new Dictionary<string, Dictionary<string, string>>());

        augmented.Should().BeSameAs(basePlan);
        augmented.Variables["ExistingVar"].Should().Be("preserved");
    }

    [Fact]
    public void Augmented_variables_overlay_existing_plan_variables()
    {
        // Plan already carries deployment-level variables. The accumulator
        // OVERLAYS the prior-step outputs without dropping the existing
        // entries. (Operators expect both kinds of variables to be
        // available in the same step's Variables dict.)
        var basePlan = NewPlan(("EnvName", "Production"));
        var outputs = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Deploy[0]"] = new() { ["BuildNumber"] = "42" },
        };

        var augmented = OutputVariableAccumulator.AugmentPlanWithPriorOutputs(
            basePlan, outputs);

        augmented.Variables["EnvName"].Should().Be("Production",
            "the existing deployment-level variable survives the merge");
        augmented.Variables["Octopus.Action[Deploy[0]].Output.BuildNumber"]
            .Should().Be("42");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static DeploymentPlan NewPlan(params (string Key, string Value)[] vars)
    {
        var dict = new Dictionary<string, string>(
            vars.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in vars) { dict[k] = v; }
        return new DeploymentPlan(
            DeploymentId:    Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps:           [],
            Variables:       dict,
            ArrayVariables:  new Dictionary<string, string[]>());
    }

    /// <summary>
    /// Builds an Octostache <see cref="VariableDictionary"/> from the
    /// plan's variables. The agent does the equivalent inside its
    /// handler context per step; the test reproduces it so the
    /// expression evaluation path is the same as runtime.
    /// </summary>
    private static VariableDictionary ToVariableDictionary(
        IReadOnlyDictionary<string, string> variables)
    {
        var dict = new VariableDictionary();
        foreach (var (k, v) in variables)
        {
            dict[k] = v;
        }
        return dict;
    }
}

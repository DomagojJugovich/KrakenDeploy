using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B4 (T0-4) — online cross-step output variables. With the default
/// StartAfterPrevious trigger each step is its own wave (its own sub-plan
/// dispatch), and pre-B4 every dispatch carried the STATIC variable bag built
/// before any step ran — captured outputs never reached later waves, while
/// offline drops and runbooks (whole plan, one dispatch) worked. These tests
/// drive the real <see cref="KrakenDeploy.Server.Transport.DeploymentWorker"/>
/// and assert on the plans the (fake) agents actually RECEIVED — the exact
/// bag the agent's handlers and <c>$OctopusParameters</c> resolve from.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class OnlineOutputVariableFlowTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Second_wave_sub_plan_carries_the_first_steps_outputs()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness, ["s1", "s2"], ["t1"]);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["s1"] = new FakeStepResponse(
            Success: true,
            Outputs: new Dictionary<string, string> { ["Url"] = "https://made-by-s1" });

        await harness.RunDeploymentAsync(deploymentId);

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded);

        agent.ReceivedPlans.Should().HaveCount(2,
            "default StartAfterPrevious triggers make each step its own wave/dispatch");

        // (System variables legitimately include Octopus.Action[s1].Name etc. —
        // only the .Output. keys are captures.)
        agent.ReceivedPlans[0].Variables.Keys.Should().NotContain(
            k => k.Contains("].Output.", StringComparison.OrdinalIgnoreCase),
            "no outputs exist before the first wave");

        agent.ReceivedPlans[1].Variables
            .Should().ContainKey("Octopus.Action[s1].Output.Url")
            .WhoseValue.Should().Be("https://made-by-s1",
                "the second wave's sub-plan must carry the first step's capture — " +
                "this is the exact bag $OctopusParameters and config-field Octostache resolve from");
    }

    [Fact]
    public async Task Sensitive_outputs_extend_the_next_waves_sensitive_names()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness, ["s1", "s2"], ["t1"]);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["s1"] = new FakeStepResponse(
            Success: true,
            Outputs: new Dictionary<string, string>
            {
                ["Token"] = "s3cret-value",
                ["Url"]   = "https://public",
            },
            SensitiveOutputs: ["Token"]);

        await harness.RunDeploymentAsync(deploymentId);

        var wave2 = agent.ReceivedPlans[1];
        wave2.Variables["Octopus.Action[s1].Output.Token"].Should().Be("s3cret-value",
            "the agent needs the plaintext to execute the step");
        wave2.SensitiveVariableNames.Should().Contain("Octopus.Action[s1].Output.Token",
            "T0-6: the agent's redactor builds from this list — without the merged key, " +
            "wave 2's logs would echo the secret unmasked");
        wave2.SensitiveVariableNames.Should().NotContain("Octopus.Action[s1].Output.Url",
            "non-sensitive outputs must not be masked");
    }

    [Fact]
    public async Task Multi_target_outputs_stay_per_target()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var (deploymentId, targets) = await SeedAsync(harness, ["s1", "s2"], ["t1", "t2"]);
        var agent1 = harness.ConnectFakeAgent(targets[0]);
        var agent2 = harness.ConnectFakeAgent(targets[1]);
        agent1.StepResponses["s1"] = new FakeStepResponse(
            Success: true, Outputs: new Dictionary<string, string> { ["Path"] = @"C:\from-t1" });
        agent2.StepResponses["s1"] = new FakeStepResponse(
            Success: true, Outputs: new Dictionary<string, string> { ["Path"] = @"D:\from-t2" });

        await harness.RunDeploymentAsync(deploymentId);

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded);

        agent1.ReceivedPlans[1].Variables["Octopus.Action[s1].Output.Path"]
            .Should().Be(@"C:\from-t1",
                "a machine-specific output must resolve to THIS target's own capture " +
                "(parity with the agent's within-dispatch accumulator)");
        agent2.ReceivedPlans[1].Variables["Octopus.Action[s1].Output.Path"]
            .Should().Be(@"D:\from-t2");
    }

    [Fact]
    public async Task Outputs_from_a_failed_non_required_step_reach_the_cleanup_step()
    {
        // Parity with the agent's accumulator: captures merge regardless of
        // step success — a failed step's state (e.g. a rollback marker) is
        // consumed by a Condition=Always/Failure cleanup step in a later wave.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            new StepBuilder { Name = "s1", Required = false },
            new StepBuilder
            {
                Name = "cleanup",
                Required = false,
                Condition = KrakenDeploy.Execution.StepCondition.Always,
            });
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["s1"] = new FakeStepResponse(
            Success: false,
            ErrorMessage: "s1 failed but captured state",
            Outputs: new Dictionary<string, string> { ["Marker"] = "rollback-me" });

        await harness.RunDeploymentAsync(deploymentId);

        agent.ReceivedPlans.Should().HaveCountGreaterThan(1,
            "the Condition=Always cleanup step must still dispatch after the soft failure");
        agent.ReceivedPlans[^1].Variables
            .Should().ContainKey("Octopus.Action[s1].Output.Marker")
            .WhoseValue.Should().Be("rollback-me");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<(Guid DeploymentId, List<DeploymentTarget> Targets)> SeedAsync(
        OrchestratorTestHarness harness,
        string[] stepNames,
        string[] targetNames)
    {
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync(targetNames);
        var steps = stepNames.Select(n => StepBuilder.Script(n)).ToArray();
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", steps);
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);
        return (deploymentId, targets);
    }
}

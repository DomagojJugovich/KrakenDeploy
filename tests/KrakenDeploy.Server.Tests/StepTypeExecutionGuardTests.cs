using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// SC4-b pre-dispatch guard: unserved step types and misplaced RunOnServer
/// flags refuse with actionable reasons BEFORE dispatch, replacing the
/// opaque agent-side "Unknown step type" / server-side "Step has no script
/// body" failures.
/// </summary>
public sealed class StepTypeExecutionGuardTests
{
    private static DeploymentStepPlan Step(
        string name, string type, bool runOnServer = false) => new(
        Index: 0, Name: name, StepType: type,
        PackageId: "", PackageVersion: "",
        Config: new Dictionary<string, string>())
    { RunOnServer = runOnServer };

    [Fact]
    public void Unserved_step_type_refuses_with_the_type_and_step_name()
    {
        var reason = StepTypeExecutionGuard.FindViolation(
            [Step("deploy web", "Octopus.NoSuchThing")],
            _ => new StepTypeExecutionGuard.TypeFacts(
                Exists: false, ServerSide: false, SupportsRunOnServer: false));

        reason.Should().Contain("deploy web")
            .And.Contain("Octopus.NoSuchThing")
            .And.Contain("no installed step package serves");
    }

    [Fact]
    public void RunOnServer_on_a_type_without_schema_support_refuses()
    {
        var reason = StepTypeExecutionGuard.FindViolation(
            [Step("notify", "Octopus.Email", runOnServer: true)],
            _ => new StepTypeExecutionGuard.TypeFacts(
                Exists: true, ServerSide: false, SupportsRunOnServer: false));

        reason.Should().Contain("notify")
            .And.Contain("Octopus.Email")
            .And.Contain("does not support server-side execution");
    }

    [Fact]
    public void RunOnServer_on_a_schema_supporting_type_passes()
    {
        var reason = StepTypeExecutionGuard.FindViolation(
            [Step("server script", "Kraken.Script", runOnServer: true)],
            _ => new StepTypeExecutionGuard.TypeFacts(
                Exists: true, ServerSide: false, SupportsRunOnServer: true));

        reason.Should().BeNull();
    }

    [Fact]
    public void RunOnServer_on_an_intrinsically_server_side_type_passes()
    {
        var reason = StepTypeExecutionGuard.FindViolation(
            [Step("approve", "Octopus.Manual", runOnServer: true)],
            _ => new StepTypeExecutionGuard.TypeFacts(
                Exists: true, ServerSide: true, SupportsRunOnServer: false));

        reason.Should().BeNull();
    }

    [Fact]
    public void Facts_are_looked_up_once_per_distinct_type_case_insensitively()
    {
        var lookups = new List<string>();
        StepTypeExecutionGuard.FindViolation(
            [Step("a", "Kraken.Script"), Step("b", "KRAKEN.SCRIPT"), Step("c", "Octopus.IIS")],
            t =>
            {
                lookups.Add(t);
                return new StepTypeExecutionGuard.TypeFacts(true, false, false);
            });

        lookups.Should().HaveCount(2, "the second Kraken.Script spelling hits the cache");
    }

    [Fact]
    public void Fully_served_plan_passes()
    {
        var reason = StepTypeExecutionGuard.FindViolation(
            [Step("a", "Kraken.IIS"), Step("b", "Octopus.Manual")],
            t => new StepTypeExecutionGuard.TypeFacts(
                Exists: true,
                ServerSide: t.Equals("Octopus.Manual", StringComparison.OrdinalIgnoreCase),
                SupportsRunOnServer: false));

        reason.Should().BeNull();
    }
}

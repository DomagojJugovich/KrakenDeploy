using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B4 (T1-6) — server-side steps capture <c>##octopus[setVariable]</c> outputs
/// via the shared <see cref="KrakenDeploy.Execution.OctopusMessageParser"/>,
/// exactly like the agent. Drives the REAL <see cref="ServerScriptStepRunner"/>
/// against a real shell process, and the REAL orchestrator for the
/// cross-side hand-off (agent output → server step env; server output → next
/// agent wave's plan).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ServerSideOutputCaptureTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static string EchoLine(string text) => OperatingSystem.IsWindows()
        ? $"Write-Output \"{text}\""
        : $"echo \"{text}\"";

    private static (string Syntax, string? Edition) ShellFor() => OperatingSystem.IsWindows()
        ? ("PowerShell", "Desktop")
        : ("Bash", null);

    [Fact]
    public async Task Runner_captures_outputs_suppresses_markers_and_masks_sensitive_values()
    {
        // Seed a real deployment so the runner's log rows satisfy the
        // server_tasks FK and can be asserted on.
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");
        var release = await harness.SeedReleaseAsync(project.Id, "1.0", StepBuilder.Script("s1"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var runner = new ServerScriptStepRunner(
            postgres.ScopeFactory,
            new NullUiHubContext(),
            TimeProvider.System,
            NullLogger<ServerScriptStepRunner>.Instance);

        const string secret = "sup3r-s3cret-value";
        var (syntax, edition) = ShellFor();
        var body = string.Join('\n',
            EchoLine($"##octopus[setVariable name='{B64("Url")}' value='{B64("https://from-server")}']"),
            EchoLine($"##octopus[setVariable name='{B64("Token")}' value='{B64(secret)}' sensitive='True']"),
            EchoLine($"token is {secret}"),
            EchoLine("plain line"));

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Action.Script.ScriptBody"] = body,
            ["Octopus.Action.Script.Syntax"]     = syntax,
        };
        if (edition is not null)
        {
            config["Octopus.Action.PowerShell.Edition"] = edition;
        }
        var step = new DeploymentStepPlan(0, "capture", "Octopus.Script", "", "", config);

        var result = await runner.ExecuteAsync(
            deploymentId, step, new Dictionary<string, string>(),
            new SecretRedactor(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Outputs.Should().Contain("Url", "https://from-server");
        result.Outputs.Should().Contain("Token", secret);
        result.SensitiveOutputNames.Should().BeEquivalentTo(["Token"]);

        await using var db = postgres.CreateContext();
        var lines = (await KrakenDeploy.Server.Data.Services.TaskLogService
            .ReadAllAsync(db, deploymentId))
            .Select(l => l.Message)
            .ToList();

        lines.Should().Contain("plain line");
        lines.Should().NotContain(l => l.Contains("##octopus["),
            "marker lines are consumed, never logged (pre-B4 the raw marker — " +
            "including the base64 of a sensitive value — landed in the task log)");
        lines.Should().NotContain(l => l.Contains(B64(secret)),
            "the base64 form of the secret must never reach the log");
        lines.Should().Contain($"token is {SecretRedactor.Mask}",
            "a line echoing the sensitive value AFTER capture is masked (live redactor fold)");
        lines.Should().NotContain(l => l.Contains(secret),
            "the plaintext secret must never reach the log");
    }

    [Fact]
    public async Task Outputs_flow_agent_to_server_step_and_server_to_later_agent_wave()
    {
        await using var harness = new OrchestratorTestHarness(postgres);
        var project = await harness.SeedProjectAsync($"p-{Guid.NewGuid():N}"[..16]);
        var env = await harness.SeedEnvironmentAsync($"e-{Guid.NewGuid():N}"[..16]);
        var targets = await harness.SeedTargetsAsync("t1");

        // Server step: echoes the AGENT step's output (proves agent → server
        // env visibility) and captures its own (proves server → agent flow).
        var (syntax, edition) = ShellFor();
        var readAgentOutput = OperatingSystem.IsWindows()
            ? "Write-Output \"from-agent=$($OctopusParameters['Octopus.Action[s1].Output.Url'])\""
            : "echo \"from-agent=$(printenv 'Octopus.Action[s1].Output.Url')\"";
        var serverBody = string.Join('\n',
            readAgentOutput,
            EchoLine($"##octopus[setVariable name='{B64("ServerStamp")}' value='{B64("stamped-by-server")}']"));
        var serverConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Action.RunOnServer"]       = "true",
            ["Octopus.Action.Script.ScriptBody"] = serverBody,
            ["Octopus.Action.Script.Syntax"]     = syntax,
        };
        if (edition is not null)
        {
            serverConfig["Octopus.Action.PowerShell.Edition"] = edition;
        }

        var release = await harness.SeedReleaseAsync(project.Id, "1.0",
            StepBuilder.Script("s1"),
            new StepBuilder { Name = "server-mid", Config = serverConfig },
            StepBuilder.Script("s3"));
        var deploymentId = await harness.CreateDeploymentAsync(release.Id, env.Id, targets);

        var agent = harness.ConnectFakeAgent(targets[0]);
        agent.StepResponses["s1"] = new FakeStepResponse(
            Success: true,
            Outputs: new Dictionary<string, string> { ["Url"] = "https://from-agent" });

        await harness.RunDeploymentAsync(deploymentId);

        (await harness.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded);

        // Agent output visible to the server step's $OctopusParameters/env.
        // Logs may already be compacted into blobs at terminal — read through
        // the stitching API.
        await using var db = postgres.CreateContext();
        var lines = (await KrakenDeploy.Server.Data.Services.TaskLogService
            .ReadAllAsync(db, deploymentId))
            .Select(l => l.Message)
            .ToList();
        lines.Should().Contain("from-agent=https://from-agent",
            "a server-side step must resolve a prior agent step's output");

        // Server capture visible to the LAST agent wave's sub-plan…
        agent.ReceivedPlans[^1].Variables
            .Should().ContainKey("Octopus.Action[server-mid].Output.ServerStamp")
            .WhoseValue.Should().Be("stamped-by-server",
                "a later agent wave must see the server step's captured output");

        // …and persisted through the shared store for the UI outputs tab.
        var row = await db.TaskOutputVariables.IgnoreQueryFilters()
            .SingleAsync(o => o.TaskId == deploymentId && o.Name == "ServerStamp");
        row.Value.Should().Be("stamped-by-server");
        row.IsSensitive.Should().BeFalse();
    }
}

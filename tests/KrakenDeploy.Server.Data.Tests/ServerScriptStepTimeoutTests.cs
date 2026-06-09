using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Online regression guard for the per-step <c>TimeoutSeconds</c> reporting bug.
/// A server-side script step that exceeds its timeout must surface as a TIMEOUT,
/// not a generic failure: <c>DeploymentWorker.RunServerWaveAsync</c> maps the
/// runner's <see cref="StepRetryRunner.Outcome{TResult}.TimedOut"/> straight to
/// <c>StepOutcomeKind.TimedOut</c> + the <c>DeploymentStepTimedOut</c> audit.
/// <para>
/// Before the fix, <see cref="ServerScriptStepRunner"/>'s <c>catch (Exception)</c>
/// swallowed the <see cref="OperationCanceledException"/> raised by
/// <c>WaitForExitAsync</c> when the per-attempt linked CTS cancelled, returned
/// <c>false</c>, and the timeout was mis-reported as Failed. This drives the REAL
/// runner through the SAME <see cref="StepRetryRunner"/> wiring the worker uses
/// (<c>RunServerStepWithRetriesAsync</c>) against a real sleeping shell process.
/// </para>
/// <para>
/// NB: the fix does not kill the spawned process on timeout (pre-existing, out of
/// scope), so this test leaves a short-lived sleeping shell process that self-exits
/// a few seconds after the test completes.
/// </para>
/// </summary>
[Collection("Postgres")]
public sealed class ServerScriptStepTimeoutTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Server_script_step_exceeding_TimeoutSeconds_yields_TimedOut()
    {
        var runner = new ServerScriptStepRunner(
            postgres.ScopeFactory,
            new NullUiHubContext(),
            TimeProvider.System,
            NullLogger<ServerScriptStepRunner>.Instance);

        // Cross-platform "sleep well past the timeout" script: powershell.exe
        // (Desktop) is always present on Windows; bash is present on the Linux
        // containers the rest of this suite runs on. The body outlives the 1s
        // per-attempt timeout by a wide margin so the cancel always wins.
        var (syntax, body, edition) = OperatingSystem.IsWindows()
            ? ("PowerShell", "Start-Sleep -Seconds 10", "Desktop")
            : ("Bash", "sleep 10", (string?)null);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Action.Script.ScriptBody"] = body,
            ["Octopus.Action.Script.Syntax"]     = syntax,
        };
        if (edition is not null)
        {
            config["Octopus.Action.PowerShell.Edition"] = edition;
        }

        var step = new DeploymentStepPlan(0, "Sleep", "Octopus.Script", "", "", config);
        var planVars = new Dictionary<string, string>();

        // Mirror DeploymentWorker.RunServerStepWithRetriesAsync: StepRetryRunner
        // wraps the runner with a per-attempt timeout (1s) and no retries.
        var outcome = await StepRetryRunner.RunAsync<bool>(
            stepName:                step.Name,
            maxRetries:              0,
            retryDelaySeconds:       0,
            timeoutSeconds:          1,
            runAttempt:              ct => runner.ExecuteAsync(Guid.NewGuid(), step, planVars, ct),
            isSuccess:               ok => ok,
            onTimeoutResult:         () => false,
            onAttemptTimedOutAsync:  null,
            onRetryAsync:            null,
            onLateSuccessAsync:      null,
            ct:                      CancellationToken.None);

        outcome.TimedOut.Should().BeTrue(
            "a server-side script step that exceeds TimeoutSeconds must surface as " +
            "TimedOut — RunServerWaveAsync maps this to StepOutcomeKind.TimedOut + the " +
            "DeploymentStepTimedOut audit — instead of being swallowed into a Failed");
        outcome.Result.Should().BeFalse("the timed-out attempt is a failed result");
    }
}

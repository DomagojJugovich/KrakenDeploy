using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
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
/// B7 closed the companion leak: the runner now KILLS the spawned process tree
/// when the wait is cancelled (see the kill test below), so a timed-out step no
/// longer leaves an orphan shell mutating server-side state.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
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
        var outcome = await StepRetryRunner.RunAsync<ServerScriptResult>(
            stepName:                step.Name,
            maxRetries:              0,
            retryDelaySeconds:       0,
            timeoutSeconds:          1,
            runAttempt:              ct => runner.ExecuteAsync(Guid.NewGuid(), step, planVars, new SecretRedactor(), ct),
            isSuccess:               r => r.Success,
            onTimeoutResult:         () => ServerScriptResult.Failure,
            onAttemptTimedOutAsync:  null,
            onRetryAsync:            null,
            onLateSuccessAsync:      null,
            ct:                      CancellationToken.None);

        outcome.TimedOut.Should().BeTrue(
            "a server-side script step that exceeds TimeoutSeconds must surface as " +
            "TimedOut — RunServerWaveAsync maps this to StepOutcomeKind.TimedOut + the " +
            "DeploymentStepTimedOut audit — instead of being swallowed into a Failed");
        outcome.Result.Success.Should().BeFalse("the timed-out attempt is a failed result");
    }

    [Fact]
    public async Task Timed_out_server_step_kills_the_spawned_process()
    {
        // B7: WaitForExitAsync(ct) only stops WAITING — pre-B7 every per-step
        // timeout and deployment cancel leaked the shell process, which kept
        // running (and kept mutating server-side state). The script publishes
        // its own PID and sleeps far past the timeout; after the timed-out
        // outcome the PID must be gone.
        var runner = new ServerScriptStepRunner(
            postgres.ScopeFactory,
            new NullUiHubContext(),
            TimeProvider.System,
            NullLogger<ServerScriptStepRunner>.Instance);

        var pidFile = Path.Combine(
            Path.GetTempPath(), $"kraken-srv-kill-{Guid.NewGuid():N}.pid");
        var (syntax, body, edition) = OperatingSystem.IsWindows()
            ? ("PowerShell",
               $"Set-Content -LiteralPath '{pidFile}' -Value $PID\nStart-Sleep -Seconds 120",
               "Desktop")
            : ("Bash", $"echo $$ > '{pidFile}'\nsleep 120", (string?)null);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Action.Script.ScriptBody"] = body,
            ["Octopus.Action.Script.Syntax"]     = syntax,
        };
        if (edition is not null)
        {
            config["Octopus.Action.PowerShell.Edition"] = edition;
        }
        var step = new DeploymentStepPlan(0, "SleepForever", "Octopus.Script", "", "", config);

        try
        {
            var outcome = await StepRetryRunner.RunAsync<ServerScriptResult>(
                stepName:                step.Name,
                maxRetries:              0,
                retryDelaySeconds:       0,
                timeoutSeconds:          3,
                runAttempt:              ct => runner.ExecuteAsync(
                    Guid.NewGuid(), step, new Dictionary<string, string>(),
                    new SecretRedactor(), ct),
                isSuccess:               r => r.Success,
                onTimeoutResult:         () => ServerScriptResult.Failure,
                onAttemptTimedOutAsync:  null,
                onRetryAsync:            null,
                onLateSuccessAsync:      null,
                ct:                      CancellationToken.None);

            outcome.TimedOut.Should().BeTrue();

            File.Exists(pidFile).Should().BeTrue("the script must have started");
            var pid = int.Parse(
                (await File.ReadAllTextAsync(pidFile)).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);

            var gone = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    if (p.HasExited) { gone = true; break; }
                }
                catch (ArgumentException)
                {
                    gone = true;
                    break;
                }
                await Task.Delay(100);
            }
            gone.Should().BeTrue("the timed-out script's process must be killed, not orphaned");
        }
        finally
        {
            try { File.Delete(pidFile); } catch (IOException) { }
        }
    }
}

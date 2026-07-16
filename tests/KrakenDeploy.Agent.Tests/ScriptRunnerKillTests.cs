using System.Diagnostics;
using FluentAssertions;
using KrakenDeploy.Steps.Common;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// B6 — <see cref="ScriptRunner"/> must KILL the child process tree on
/// cancellation. Pre-B6, <c>WaitForExitAsync(ct)</c> only stopped waiting: every
/// cancel and per-step timeout leaked an orphan process that kept running to
/// completion (the audit's orphan-leak finding). Runs a REAL PowerShell process,
/// same pattern as the server-side <c>ServerScriptStepTimeoutTests</c>.
/// </summary>
public sealed class ScriptRunnerKillTests
{
    [Fact]
    public async Task Cancellation_kills_the_script_process()
    {
        var pidFile = Path.Combine(
            Path.GetTempPath(), $"kraken-kill-test-{Guid.NewGuid():N}.pid");
        // The script publishes its own PID, then sleeps far longer than the
        // test — if the kill doesn't happen, the PID stays alive and the
        // assertion below fails.
        var script =
            $"Set-Content -LiteralPath '{pidFile}' -Value $PID\n" +
            "Start-Sleep -Seconds 120";

        using var cts = new CancellationTokenSource();
        var runner = new ScriptRunner();
        var run = runner.RunAsync(
            script, "PowerShell", Path.GetTempPath(),
            new Dictionary<string, string>(),
            (_, _) => Task.CompletedTask,
            cts.Token,
            powerShellEdition: "Desktop");

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(pidFile))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("script never published its PID");
                }
                await Task.Delay(50);
            }
            var pid = int.Parse(
                (await File.ReadAllTextAsync(pidFile)).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);

            cts.Cancel();
            await FluentActions
                .Awaiting(() => run)
                .Should().ThrowAsync<OperationCanceledException>();

            // The tree kill + 10 s reap must actually terminate the process.
            var gone = false;
            var killDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < killDeadline)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.HasExited)
                    {
                        gone = true;
                        break;
                    }
                }
                catch (ArgumentException)
                {
                    gone = true; // no such process — killed and reaped
                    break;
                }
                await Task.Delay(100);
            }
            gone.Should().BeTrue(
                "the cancelled script's process must be killed, not orphaned");
        }
        finally
        {
            try { File.Delete(pidFile); } catch (IOException) { }
        }
    }
}

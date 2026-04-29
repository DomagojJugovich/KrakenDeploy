using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Runs a PowerShell or Bash script in a child process, capturing stdout and
/// stderr line-by-line and forwarding them to the caller via a callback.
/// </summary>
public sealed class ScriptRunner(ILogger<ScriptRunner> logger)
{
    /// <summary>
    /// Executes the script and returns <c>true</c> if the process exited with code 0.
    /// </summary>
    /// <param name="scriptBody">Script text to execute.</param>
    /// <param name="syntax">"PowerShell" or "Bash".</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="environmentVariables">Extra env vars to inject.</param>
    /// <param name="onOutput">Callback invoked for each output line (level, message).</param>
    /// <param name="ct">Cancellation token; kills the process on cancellation.</param>
    public async Task<bool> RunAsync(
        string scriptBody,
        string syntax,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        Func<string, string, Task> onOutput,   // (level, message)
        CancellationToken ct)
    {
        var scriptFile = WriteScriptFile(scriptBody, syntax);
        try
        {
            return await ExecuteAsync(scriptFile, syntax, workingDirectory,
                environmentVariables, onOutput, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(scriptFile);
        }
    }

    // ── Script file ────────────────────────────────────────────────────────

    private static string WriteScriptFile(string body, string syntax)
    {
        var ext = syntax.Equals("Bash", StringComparison.OrdinalIgnoreCase) ? ".sh" : ".ps1";
        var path = Path.Combine(Path.GetTempPath(), $"kraken-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, body);
        return path;
    }

    // ── Process execution ──────────────────────────────────────────────────

    private async Task<bool> ExecuteAsync(
        string scriptFile,
        string syntax,
        string workingDirectory,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        var (exe, args) = BuildCommand(scriptFile, syntax);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var (k, v) in envVars)
        {
            psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var outputTasks = new List<Task>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputTasks.Add(onOutput("info", e.Data));
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputTasks.Add(onOutput("error", e.Data));
            }
        };

        logger.LogDebug("Starting script process: {Exe} {Args}", exe, args);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        // Drain any queued output callbacks.
        await Task.WhenAll(outputTasks).ConfigureAwait(false);

        var exitCode = process.ExitCode;
        logger.LogDebug("Script exited with code {ExitCode}.", exitCode);
        return exitCode == 0;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (string exe, string args) BuildCommand(string scriptFile, string syntax)
    {
        if (syntax.Equals("Bash", StringComparison.OrdinalIgnoreCase))
        {
            return ("bash", $"\"{scriptFile}\"");
        }

        // PowerShell — use `pwsh` (cross-platform PS 7+).
        return ("pwsh", $"-NonInteractive -NoProfile -File \"{scriptFile}\"");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}

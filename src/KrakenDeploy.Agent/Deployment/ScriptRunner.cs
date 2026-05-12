using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Runs a script in a child process, capturing stdout and stderr line-by-line
/// and forwarding them to the caller via a callback. Supported syntaxes:
/// PowerShell (Desktop or Core), Bash, CSharp (dotnet-script), FSharp (dotnet fsi),
/// Python.
/// </summary>
public sealed class ScriptRunner(ILogger<ScriptRunner> logger)
{
    /// <summary>
    /// Executes the script and returns <c>true</c> if the process exited with code 0.
    /// </summary>
    /// <param name="scriptBody">Script text to execute.</param>
    /// <param name="syntax">"PowerShell", "Bash", "CSharp", "FSharp", or "Python".</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="environmentVariables">Extra env vars to inject.</param>
    /// <param name="onOutput">Callback invoked for each output line (level, message).</param>
    /// <param name="ct">Cancellation token; kills the process on cancellation.</param>
    /// <param name="powerShellEdition">
    /// "Desktop" (Windows PowerShell 5.x via <c>powershell.exe</c>) or "Core" (pwsh 7+).
    /// Ignored for non-PowerShell syntaxes. Null defaults to Core.
    /// </param>
    public async Task<bool> RunAsync(
        string scriptBody,
        string syntax,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        Func<string, string, Task> onOutput,   // (level, message)
        CancellationToken ct,
        string? powerShellEdition = null)
    {
        var scriptFile = WriteScriptFile(scriptBody, syntax);
        try
        {
            return await ExecuteAsync(scriptFile, syntax, powerShellEdition, workingDirectory,
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
        var ext = syntax.ToLowerInvariant() switch
        {
            "bash"   => ".sh",
            "csharp" => ".csx",
            "fsharp" => ".fsx",
            "python" => ".py",
            _        => ".ps1",
        };
        var path = Path.Combine(Path.GetTempPath(), $"kraken-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, body);
        return path;
    }

    // ── Process execution ──────────────────────────────────────────────────

    private async Task<bool> ExecuteAsync(
        string scriptFile,
        string syntax,
        string? powerShellEdition,
        string workingDirectory,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        var (exe, args) = BuildCommand(scriptFile, syntax, powerShellEdition);

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

    private static (string exe, string args) BuildCommand(
        string scriptFile, string syntax, string? powerShellEdition)
    {
        switch (syntax.ToLowerInvariant())
        {
            case "bash":
                return ("bash", $"\"{scriptFile}\"");

            case "csharp":
                // dotnet-script must be installed as a global tool:
                //   dotnet tool install -g dotnet-script
                return ("dotnet", $"script \"{scriptFile}\"");

            case "fsharp":
                return ("dotnet", $"fsi \"{scriptFile}\"");

            case "python":
                return ("python", $"\"{scriptFile}\"");

            default:
                // PowerShell — pick the executable by edition.
                var wantDesktop = "Desktop".Equals(
                    powerShellEdition, StringComparison.OrdinalIgnoreCase);

                if (wantDesktop && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows PowerShell 5.x — Windows-only.
                    return ("powershell.exe",
                        $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"");
                }

                // Cross-platform pwsh 7+ (also fallback when Desktop requested off-Windows).
                return ("pwsh", $"-NonInteractive -NoProfile -File \"{scriptFile}\"");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}

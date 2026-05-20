using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Steps.Script;

/// <summary>
/// Runs a script in a child process, capturing stdout and stderr line-by-line
/// and forwarding them to the caller via a callback. Supported syntaxes:
/// PowerShell (Desktop or Core), Bash, CSharp (dotnet-script), FSharp (dotnet fsi),
/// Python.
/// <para>
/// This is the step-package copy (Phase D-8.4). The legacy agent-side
/// <c>KrakenDeploy.Agent.Deployment.ScriptRunner</c> is still used by
/// the in-DI KrakenIis + WindowsService handlers until those are ported
/// (D-8.6 / D-8.7); when they migrate, the two copies converge.
/// </para>
/// </summary>
internal sealed class ScriptRunner
{
    private readonly ILogger _logger;

    /// <summary>
    /// Constructs the runner with a logger; if step packages can't resolve
    /// ILogger&lt;ScriptRunner&gt; through the host's DI (they don't see it),
    /// callers pass <see cref="NullLogger.Instance"/> or build a logger via
    /// the agent's ILoggerFactory at construction time.
    /// </summary>
    public ScriptRunner(ILogger? logger = null)
        => _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Executes the script and returns <c>true</c> if the process exited with code 0.
    /// </summary>
    public async Task<bool> RunAsync(
        string scriptBody,
        string syntax,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        Func<string, string, Task> onOutput,
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
            FileName               = exe,
            Arguments              = args,
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        foreach (var (k, v) in envVars)
        {
            psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var outputTasks = new List<Task>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) { outputTasks.Add(onOutput("info", e.Data)); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) { outputTasks.Add(onOutput("error", e.Data)); }
        };

        _logger.LogDebug("Starting script process: {Exe} {Args}", exe, args);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(outputTasks).ConfigureAwait(false);

        var exitCode = process.ExitCode;
        _logger.LogDebug("Script exited with code {ExitCode}.", exitCode);
        return exitCode == 0;
    }

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
                    return ("powershell.exe",
                        $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"");
                }

                return ("pwsh", $"-NonInteractive -NoProfile -File \"{scriptFile}\"");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

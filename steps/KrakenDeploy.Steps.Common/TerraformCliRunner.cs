using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KrakenDeploy.Steps.Common;

public static class TerraformCliRunner
{
    public static async Task<bool> RunAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var exitCode = await RunAndReturnExitCodeAsync(
            arguments, workingDirectory, onOutput, ct, environmentVariables).ConfigureAwait(false);
        return exitCode == 0;
    }

    public static async Task<int> RunAndReturnExitCodeAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var terraformPath = ResolveBinaryPath();
        var fullArgs = $"{arguments} -no-color";

        var psi = new ProcessStartInfo
        {
            FileName = terraformPath,
            Arguments = fullArgs,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.EnvironmentVariables["TF_IN_AUTOMATION"] = "true";
        psi.EnvironmentVariables["TF_INPUT"] = "false";

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                psi.EnvironmentVariables[key] = value;
            }
        }

        await onOutput("info", $"terraform {fullArgs}").ConfigureAwait(false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = StreamOutputAsync(process.StandardOutput, "info", onOutput, ct);
        var stderrTask = StreamOutputAsync(process.StandardError, "error", onOutput, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    private static async Task StreamOutputAsync(
        StreamReader reader, string level,
        Func<string, string, Task> onOutput, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                await onOutput(level, line).ConfigureAwait(false);
            }
        }
    }

    private static string ResolveBinaryPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "terraform.exe";
        }

        return "terraform";
    }
}

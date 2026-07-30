using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KrakenDeploy.Steps.Common;

public static class DockerCliRunner
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
        var dockerPath = ResolveDockerPath();

        var psi = new ProcessStartInfo
        {
            FileName = dockerPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                psi.EnvironmentVariables[key] = value;
            }
        }

        await onOutput("info", $"docker {arguments}").ConfigureAwait(false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = StreamOutputAsync(process.StandardOutput, "info", onOutput, ct);
        var stderrTask = StreamOutputAsync(process.StandardError, "error", onOutput, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    public static async Task<bool> IsAvailableAsync(
        Func<string, string, Task> onOutput, CancellationToken ct)
    {
        try
        {
            return await RunAsync("version --format '{{.Client.Version}}'", ".", onOutput, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
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

    private static string ResolveDockerPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "docker.exe";
        }

        return "docker";
    }
}

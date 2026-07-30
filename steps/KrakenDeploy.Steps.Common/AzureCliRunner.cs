using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KrakenDeploy.Steps.Common;

public static class AzureCliRunner
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
        var azPath = ResolveBinaryPath();
        var fullArgs = $"{arguments} --output none";

        var psi = new ProcessStartInfo
        {
            FileName = azPath,
            Arguments = fullArgs,
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

        await onOutput("info", $"az {fullArgs}").ConfigureAwait(false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = StreamOutputAsync(process.StandardOutput, "info", onOutput, ct);
        var stderrTask = StreamOutputAsync(process.StandardError, "error", onOutput, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    public static async Task<bool> LoginAsync(
        string? servicePrincipalAppId,
        string? servicePrincipalPassword,
        string? tenantId,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(servicePrincipalAppId)
            || string.IsNullOrWhiteSpace(servicePrincipalPassword))
        {
            return true;
        }

        var args = $"login --service-principal --username {servicePrincipalAppId} --password {servicePrincipalPassword}";
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            args += $" --tenant {tenantId}";
        }

        await onOutput("info", "Logging in to Azure via service principal...").ConfigureAwait(false);

        var exitCode = await RunAndReturnExitCodeRawAsync(args, ".", onOutput, ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            await onOutput("error", "Azure login failed.").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static async Task<int> RunAndReturnExitCodeRawAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        var azPath = ResolveBinaryPath();

        var psi = new ProcessStartInfo
        {
            FileName = azPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

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
            return "az.cmd";
        }

        return "az";
    }
}

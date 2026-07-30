using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KrakenDeploy.Steps.Common;

public static class AwsCliRunner
{
    public static async Task<bool> RunAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        AwsCredentials? credentials = null,
        string? region = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var exitCode = await RunAndReturnExitCodeAsync(
            arguments, workingDirectory, onOutput, ct, credentials, region, environmentVariables)
            .ConfigureAwait(false);
        return exitCode == 0;
    }

    public static async Task<int> RunAndReturnExitCodeAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        AwsCredentials? credentials = null,
        string? region = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var awsPath = ResolveBinaryPath();

        var fullArgs = BuildArgs(arguments, region);

        var psi = new ProcessStartInfo
        {
            FileName = awsPath,
            Arguments = fullArgs,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (credentials is not null)
        {
            psi.EnvironmentVariables["AWS_ACCESS_KEY_ID"] = credentials.AccessKeyId;
            psi.EnvironmentVariables["AWS_SECRET_ACCESS_KEY"] = credentials.SecretAccessKey;
            if (!string.IsNullOrEmpty(credentials.SessionToken))
            {
                psi.EnvironmentVariables["AWS_SESSION_TOKEN"] = credentials.SessionToken;
            }
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                psi.EnvironmentVariables[key] = value;
            }
        }

        await onOutput("info", $"aws {fullArgs}").ConfigureAwait(false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = StreamOutputAsync(process.StandardOutput, "info", onOutput, ct);
        var stderrTask = StreamOutputAsync(process.StandardError, "error", onOutput, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    private static string BuildArgs(string arguments, string? region)
    {
        if (!string.IsNullOrEmpty(region))
        {
            return $"{arguments} --region {region} --no-cli-pager";
        }

        return $"{arguments} --no-cli-pager";
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
            return "aws.exe";
        }

        return "aws";
    }
}

public sealed record AwsCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string? SessionToken = null);

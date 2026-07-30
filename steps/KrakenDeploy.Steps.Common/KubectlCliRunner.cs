using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace KrakenDeploy.Steps.Common;

public static class KubectlCliRunner
{
    public static async Task<bool> RunAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        string? kubeconfigPath = null,
        string? kubeContext = null,
        string? namespaceOverride = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var exitCode = await RunAndReturnExitCodeAsync(
            arguments, workingDirectory, onOutput, ct,
            kubeconfigPath, kubeContext, namespaceOverride, environmentVariables).ConfigureAwait(false);
        return exitCode == 0;
    }

    public static async Task<int> RunAndReturnExitCodeAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        string? kubeconfigPath = null,
        string? kubeContext = null,
        string? namespaceOverride = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var kubectlPath = ResolveBinaryPath("kubectl");

        var fullArgs = BuildArgs(arguments, kubeconfigPath, kubeContext, namespaceOverride);

        var psi = new ProcessStartInfo
        {
            FileName = kubectlPath,
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

        await onOutput("info", $"kubectl {fullArgs}").ConfigureAwait(false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = StreamOutputAsync(process.StandardOutput, "info", onOutput, ct);
        var stderrTask = StreamOutputAsync(process.StandardError, "error", onOutput, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    public static async Task<bool> RunHelmAsync(
        string arguments,
        string workingDirectory,
        Func<string, string, Task> onOutput,
        CancellationToken ct,
        string? kubeconfigPath = null,
        string? kubeContext = null,
        string? namespaceOverride = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var helmPath = ResolveBinaryPath("helm");

        var fullArgs = BuildArgs(arguments, kubeconfigPath, kubeContext, namespaceOverride);

        var psi = new ProcessStartInfo
        {
            FileName = helmPath,
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

        await onOutput("info", $"helm {fullArgs}").ConfigureAwait(false);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = StreamOutputAsync(process.StandardOutput, "info", onOutput, ct);
        var stderrTask = StreamOutputAsync(process.StandardError, "error", onOutput, ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode == 0;
    }

    public static string WriteTemporaryKubeconfig(
        string clusterUrl,
        string? token,
        string? clientCertData,
        string? clientKeyData,
        string? caCertData,
        string tempDir)
    {
        var kubeconfigPath = Path.Combine(tempDir, "kubeconfig.yaml");

        var yaml = new System.Text.StringBuilder();
        yaml.AppendLine("apiVersion: v1");
        yaml.AppendLine("kind: Config");
        yaml.AppendLine("clusters:");
        yaml.AppendLine("- cluster:");
        yaml.AppendLine(CultureInfo.InvariantCulture, $"    server: {clusterUrl}");

        if (!string.IsNullOrEmpty(caCertData))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    certificate-authority-data: {caCertData}");
        }
        else
        {
            yaml.AppendLine("    insecure-skip-tls-verify: true");
        }

        yaml.AppendLine("  name: kraken-cluster");
        yaml.AppendLine("contexts:");
        yaml.AppendLine("- context:");
        yaml.AppendLine("    cluster: kraken-cluster");
        yaml.AppendLine("    user: kraken-user");
        yaml.AppendLine("  name: kraken-context");
        yaml.AppendLine("current-context: kraken-context");
        yaml.AppendLine("users:");
        yaml.AppendLine("- name: kraken-user");
        yaml.AppendLine("  user:");

        if (!string.IsNullOrEmpty(token))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    token: {token}");
        }
        else if (!string.IsNullOrEmpty(clientCertData) && !string.IsNullOrEmpty(clientKeyData))
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    client-certificate-data: {clientCertData}");
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    client-key-data: {clientKeyData}");
        }

        File.WriteAllText(kubeconfigPath, yaml.ToString());
        return kubeconfigPath;
    }

    private static string BuildArgs(
        string arguments, string? kubeconfigPath, string? kubeContext, string? namespaceOverride)
    {
        var parts = new List<string> { arguments };

        if (!string.IsNullOrEmpty(kubeconfigPath))
        {
            parts.Add($"--kubeconfig \"{kubeconfigPath}\"");
        }

        if (!string.IsNullOrEmpty(kubeContext))
        {
            parts.Add($"--context {kubeContext}");
        }

        if (!string.IsNullOrEmpty(namespaceOverride))
        {
            parts.Add($"--namespace {namespaceOverride}");
        }

        return string.Join(" ", parts);
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

    private static string ResolveBinaryPath(string binary)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"{binary}.exe";
        }

        return binary;
    }
}

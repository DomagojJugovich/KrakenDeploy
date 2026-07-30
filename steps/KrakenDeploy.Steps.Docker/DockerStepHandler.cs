using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;

namespace KrakenDeploy.Steps.Docker;

public static class DockerConfigKeys
{
    private const string Prefix = "Octopus.Action.Docker.";

    public const string Image = Prefix + "Image";
    public const string Tag = Prefix + "Tag";
    public const string ContainerName = Prefix + "ContainerName";
    public const string Command = Prefix + "Command";
    public const string EntryPoint = Prefix + "EntryPoint";
    public const string EnvVars = Prefix + "EnvVars";
    public const string Volumes = Prefix + "Volumes";
    public const string Ports = Prefix + "Ports";
    public const string Network = Prefix + "Network";
    public const string NetworkName = Prefix + "NetworkName";
    public const string NetworkDriver = Prefix + "NetworkDriver";
    public const string Labels = Prefix + "Labels";
    public const string RestartPolicy = Prefix + "RestartPolicy";
    public const string Detach = Prefix + "Detach";
    public const string RemoveOnStop = Prefix + "RemoveOnStop";
    public const string StopTimeout = Prefix + "StopTimeout";
    public const string ResourceType = Prefix + "ResourceType";
    public const string ResourceName = Prefix + "ResourceName";
    public const string RegistryUrl = Prefix + "RegistryUrl";
    public const string RegistryUsername = Prefix + "RegistryUsername";
    public const string RegistryPassword = Prefix + "RegistryPassword";
    public const string AdditionalArgs = Prefix + "AdditionalArgs";
}

public sealed class DockerStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.DockerRun", StringComparison.OrdinalIgnoreCase)
        || stepType.Equals("Octopus.DockerStop", StringComparison.OrdinalIgnoreCase)
        || stepType.Equals("Octopus.DockerNetwork", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        return context.Step.StepType.ToLowerInvariant() switch
        {
            "octopus.dockerrun" => await HandleRunAsync(context, ct).ConfigureAwait(false),
            "octopus.dockerstop" => await HandleStopAsync(context, ct).ConfigureAwait(false),
            "octopus.dockernetwork" => await HandleNetworkAsync(context, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleRunAsync(StepHandlerContext context, CancellationToken ct)
    {
        var image = Get(context, DockerConfigKeys.Image);
        if (string.IsNullOrWhiteSpace(image))
        {
            await context.LogAsync("error", "Octopus.Action.Docker.Image is required.").ConfigureAwait(false);
            return false;
        }

        var tag = Get(context, DockerConfigKeys.Tag);
        var fullImage = string.IsNullOrWhiteSpace(tag) ? image : string.Create(CultureInfo.InvariantCulture, $"{image}:{tag}");

        if (!await TryLoginAsync(context, ct).ConfigureAwait(false))
        {
            return false;
        }

        await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Pulling image {fullImage}...")).ConfigureAwait(false);
        if (!await DockerCliRunner.RunAsync(string.Create(CultureInfo.InvariantCulture, $"pull {fullImage}"), ".", context.LogAsync, ct).ConfigureAwait(false))
        {
            await context.LogAsync("error", string.Create(CultureInfo.InvariantCulture, $"Failed to pull image {fullImage}.")).ConfigureAwait(false);
            return false;
        }

        var args = BuildRunArgs(context, fullImage);
        await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Running container from {fullImage}...")).ConfigureAwait(false);
        return await DockerCliRunner.RunAsync(args, ".", context.LogAsync, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HandleStopAsync(StepHandlerContext context, CancellationToken ct)
    {
        var resourceType = Get(context, DockerConfigKeys.ResourceType) ?? "container";
        var name = Get(context, DockerConfigKeys.ResourceName)
            ?? Get(context, DockerConfigKeys.ContainerName);

        if (string.IsNullOrWhiteSpace(name))
        {
            await context.LogAsync("error",
                "Octopus.Action.Docker.ResourceName (or ContainerName) is required.")
                .ConfigureAwait(false);
            return false;
        }

        var remove = ParseBool(Get(context, DockerConfigKeys.RemoveOnStop));
        var timeout = Get(context, DockerConfigKeys.StopTimeout) ?? "10";

        if (resourceType.Equals("network", StringComparison.OrdinalIgnoreCase))
        {
            await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Removing network '{name}'...")).ConfigureAwait(false);
            return await DockerCliRunner.RunAsync(string.Create(CultureInfo.InvariantCulture, $"network rm {name}"), ".", context.LogAsync, ct)
                .ConfigureAwait(false);
        }

        await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Stopping container '{name}' (timeout {timeout}s)..."))
            .ConfigureAwait(false);
        var stopped = await DockerCliRunner.RunAsync(string.Create(CultureInfo.InvariantCulture, $"stop -t {timeout} {name}"), ".", context.LogAsync, ct)
            .ConfigureAwait(false);

        if (remove)
        {
            await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Removing container '{name}'...")).ConfigureAwait(false);
            var removed = await DockerCliRunner.RunAsync(string.Create(CultureInfo.InvariantCulture, $"rm -f {name}"), ".", context.LogAsync, ct)
                .ConfigureAwait(false);
            return stopped && removed;
        }

        return stopped;
    }

    private static async Task<bool> HandleNetworkAsync(StepHandlerContext context, CancellationToken ct)
    {
        var name = Get(context, DockerConfigKeys.NetworkName);
        if (string.IsNullOrWhiteSpace(name))
        {
            await context.LogAsync("error",
                "Octopus.Action.Docker.NetworkName is required.").ConfigureAwait(false);
            return false;
        }

        var driver = Get(context, DockerConfigKeys.NetworkDriver) ?? "bridge";

        var sb = new StringBuilder("network create");
        sb.Append(CultureInfo.InvariantCulture, $" --driver {driver}");

        var labels = Get(context, DockerConfigKeys.Labels);
        if (!string.IsNullOrWhiteSpace(labels))
        {
            foreach (var label in SplitLines(labels))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --label {label}");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $" {name}");

        await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Creating Docker network '{name}' (driver: {driver})..."))
            .ConfigureAwait(false);
        return await DockerCliRunner.RunAsync(sb.ToString(), ".", context.LogAsync, ct).ConfigureAwait(false);
    }

    private static async Task<bool> TryLoginAsync(StepHandlerContext context, CancellationToken ct)
    {
        var registry = Get(context, DockerConfigKeys.RegistryUrl);
        var username = Get(context, DockerConfigKeys.RegistryUsername);
        var password = Get(context, DockerConfigKeys.RegistryPassword);

        if (string.IsNullOrWhiteSpace(registry) || string.IsNullOrWhiteSpace(username))
        {
            return true;
        }

        await context.LogAsync("info", string.Create(CultureInfo.InvariantCulture, $"Logging in to registry {registry}...")).ConfigureAwait(false);
        var args = string.Create(CultureInfo.InvariantCulture, $"login {registry} --username {username} --password-stdin");

        var exitCode = await RunWithStdinAsync(args, password ?? "", context.LogAsync, ct)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            await context.LogAsync("error", string.Create(CultureInfo.InvariantCulture, $"Docker login to {registry} failed.")).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static string BuildRunArgs(StepHandlerContext context, string fullImage)
    {
        var sb = new StringBuilder("run");

        var detach = Get(context, DockerConfigKeys.Detach);
        if (ParseBool(detach) || detach is null)
        {
            sb.Append(" -d");
        }

        var name = Get(context, DockerConfigKeys.ContainerName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --name \"{name}\"");
        }

        var network = Get(context, DockerConfigKeys.Network);
        if (!string.IsNullOrWhiteSpace(network))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --network \"{network}\"");
        }

        var restart = Get(context, DockerConfigKeys.RestartPolicy);
        if (!string.IsNullOrWhiteSpace(restart))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --restart {restart}");
        }

        var envVars = Get(context, DockerConfigKeys.EnvVars);
        if (!string.IsNullOrWhiteSpace(envVars))
        {
            foreach (var env in SplitLines(envVars))
            {
                sb.Append(CultureInfo.InvariantCulture, $" -e \"{env}\"");
            }
        }

        var volumes = Get(context, DockerConfigKeys.Volumes);
        if (!string.IsNullOrWhiteSpace(volumes))
        {
            foreach (var vol in SplitLines(volumes))
            {
                sb.Append(CultureInfo.InvariantCulture, $" -v \"{vol}\"");
            }
        }

        var ports = Get(context, DockerConfigKeys.Ports);
        if (!string.IsNullOrWhiteSpace(ports))
        {
            foreach (var port in SplitLines(ports))
            {
                sb.Append(CultureInfo.InvariantCulture, $" -p {port}");
            }
        }

        var labels = Get(context, DockerConfigKeys.Labels);
        if (!string.IsNullOrWhiteSpace(labels))
        {
            foreach (var label in SplitLines(labels))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --label \"{label}\"");
            }
        }

        var entrypoint = Get(context, DockerConfigKeys.EntryPoint);
        if (!string.IsNullOrWhiteSpace(entrypoint))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --entrypoint \"{entrypoint}\"");
        }

        var additional = Get(context, DockerConfigKeys.AdditionalArgs);
        if (!string.IsNullOrWhiteSpace(additional))
        {
            sb.Append(CultureInfo.InvariantCulture, $" {additional}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" {fullImage}");

        var command = Get(context, DockerConfigKeys.Command);
        if (!string.IsNullOrWhiteSpace(command))
        {
            sb.Append(CultureInfo.InvariantCulture, $" {command}");
        }

        return sb.ToString();
    }

    private static async Task<int> RunWithStdinAsync(
        string arguments, string stdin,
        Func<string, string, Task> onOutput, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "docker.exe" : "docker",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            await onOutput("info", stdout.Trim()).ConfigureAwait(false);
        }

        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            await onOutput("error", stderr.Trim()).ConfigureAwait(false);
        }

        return process.ExitCode;
    }

    private static string[] SplitLines(string raw)
        => raw.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ParseBool(string? value)
        => value is not null && (value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);
}

using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;

namespace KrakenDeploy.Steps.PackageRunner;

public static class PackageRunnerConfigKeys
{
    private const string Prefix = "Kraken.Action.PackageRunner.";

    public const string ExecutablePath = Prefix + "ExecutablePath";
    public const string Arguments = Prefix + "Arguments";
    public const string WorkingDirectory = Prefix + "WorkingDirectory";
    public const string AssemblyPath = Prefix + "AssemblyPath";
    public const string TypeName = Prefix + "TypeName";
    public const string TimeoutSeconds = Prefix + "TimeoutSeconds";
}

public sealed class PackageRunnerStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Kraken.RunPackageExecutable", StringComparison.OrdinalIgnoreCase)
        || stepType.Equals("Kraken.RunPackageAssembly", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        return context.Step.StepType.ToLowerInvariant() switch
        {
            "kraken.runpackageexecutable" => await HandleExecutableAsync(context, ct).ConfigureAwait(false),
            "kraken.runpackageassembly" => await HandleAssemblyAsync(context, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> HandleExecutableAsync(StepHandlerContext context, CancellationToken ct)
    {
        var exePath = Get(context, PackageRunnerConfigKeys.ExecutablePath);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            await context.LogAsync("error", "ExecutablePath is required.").ConfigureAwait(false);
            return false;
        }

        var fullPath = Path.IsPathRooted(exePath)
            ? exePath
            : Path.Combine(context.ExtractDir, exePath);

        if (!File.Exists(fullPath))
        {
            await context.LogAsync("error",
                string.Create(CultureInfo.InvariantCulture, $"Executable not found: {fullPath}"))
                .ConfigureAwait(false);
            return false;
        }

        var args = Get(context, PackageRunnerConfigKeys.Arguments) ?? "";
        var workDir = Get(context, PackageRunnerConfigKeys.WorkingDirectory);
        if (string.IsNullOrWhiteSpace(workDir) || !Path.IsPathRooted(workDir))
        {
            workDir = context.ExtractDir;
        }

        var timeoutSec = int.TryParse(Get(context, PackageRunnerConfigKeys.TimeoutSeconds), out var t) ? t : 600;

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Running: {Path.GetFileName(fullPath)} {args}"))
            .ConfigureAwait(false);

        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in context.Plan.Variables)
        {
            envVars[k] = v;
        }

        envVars["KRAKEN_PACKAGE_DIR"] = context.ExtractDir;
        envVars["KRAKEN_ARTIFACTS_PATH"] = context.ArtifactsDir;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var runner = new ScriptRunner();
        var script = OperatingSystem.IsWindows()
            ? $"& \"{fullPath}\" {args}"
            : $"\"{fullPath}\" {args}";

        var syntax = OperatingSystem.IsWindows() ? "PowerShell" : "Bash";

        try
        {
            return await runner.RunAsync(
                script, syntax, workDir, envVars, context.LogAsync, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await context.LogAsync("error",
                string.Create(CultureInfo.InvariantCulture, $"Process timed out after {timeoutSec}s."))
                .ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<bool> HandleAssemblyAsync(StepHandlerContext context, CancellationToken ct)
    {
        var assemblyPath = Get(context, PackageRunnerConfigKeys.AssemblyPath);
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            await context.LogAsync("error", "AssemblyPath is required.").ConfigureAwait(false);
            return false;
        }

        var fullPath = Path.IsPathRooted(assemblyPath)
            ? assemblyPath
            : Path.Combine(context.ExtractDir, assemblyPath);

        if (!File.Exists(fullPath))
        {
            await context.LogAsync("error",
                string.Create(CultureInfo.InvariantCulture, $"Assembly not found: {fullPath}"))
                .ConfigureAwait(false);
            return false;
        }

        var typeName = Get(context, PackageRunnerConfigKeys.TypeName);
        var timeoutSec = int.TryParse(Get(context, PackageRunnerConfigKeys.TimeoutSeconds), out var t) ? t : 600;

        await context.LogAsync("info",
            string.Create(CultureInfo.InvariantCulture, $"Loading assembly: {Path.GetFileName(fullPath)}"))
            .ConfigureAwait(false);

        CollectibleLoadContext? alc = null;
        try
        {
            alc = new CollectibleLoadContext(fullPath);
            var assembly = alc.LoadFromAssemblyPath(fullPath);
            var handlerType = FindHandlerType(assembly, typeName);

            if (handlerType is null)
            {
                await context.LogAsync("error",
                    string.IsNullOrWhiteSpace(typeName)
                        ? "No IStepHandler implementation found in the assembly."
                        : string.Create(CultureInfo.InvariantCulture, $"Type '{typeName}' not found or does not implement IStepHandler."))
                    .ConfigureAwait(false);
                return false;
            }

            await context.LogAsync("info",
                string.Create(CultureInfo.InvariantCulture, $"Invoking {handlerType.FullName}..."))
                .ConfigureAwait(false);

            var instance = (IStepHandler)Activator.CreateInstance(handlerType)!;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            bool success;
            try
            {
                success = await instance.HandleAsync(context, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await context.LogAsync("error",
                    string.Create(CultureInfo.InvariantCulture, $"Assembly step timed out after {timeoutSec}s."))
                    .ConfigureAwait(false);
                return false;
            }

            if (success)
            {
                await context.LogAsync("info", "Assembly step completed successfully.")
                    .ConfigureAwait(false);
            }
            else
            {
                await context.LogAsync("error", "Assembly step reported failure.")
                    .ConfigureAwait(false);
            }

            return success;
        }
        catch (Exception ex)
        {
            await context.LogAsync("error",
                string.Create(CultureInfo.InvariantCulture, $"Assembly execution failed: {ex.Message}"))
                .ConfigureAwait(false);
            return false;
        }
        finally
        {
            alc?.Unload();
        }
    }

    private static Type? FindHandlerType(Assembly assembly, string? typeName)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var t = assembly.GetType(typeName);
            if (t is not null && typeof(IStepHandler).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            {
                return t;
            }

            return null;
        }

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        var candidates = types
            .Where(t => typeof(IStepHandler).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string? Get(StepHandlerContext context, string key)
        => context.Step.Config.GetValueOrDefault(key);

    private sealed class CollectibleLoadContext(string mainAssemblyPath)
        : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyName _mainName = AssemblyName.GetAssemblyName(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.FullName == _mainName.FullName)
            {
                return null;
            }

            if (assemblyName.Name == "KrakenDeploy.Contracts")
            {
                return null;
            }

            var dir = Path.GetDirectoryName(mainAssemblyPath)!;
            var candidate = Path.Combine(dir, $"{assemblyName.Name}.dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}

using KrakenDeploy.Agent.Deployment.StepHandlers;
using Octostache;

namespace KrakenDeploy.Agent.Deployment.Service;

/// <summary>
/// Handles the <c>Octopus.WindowsService</c> step type — Argosy / WebArgosy-style
/// Windows service install/upgrade. Windows-only.
/// <para>
/// The step deploys a package (handled by <c>DeploymentExecutor</c> + extract)
/// and then configures a Windows service whose binary lives inside the deployed
/// payload. Property contract sourced from
/// <a href="https://octopus.com/docs/deployments/windows/windows-services">Octopus public docs</a>
/// (clean-room — not from Calamari source; see docs/architecture.md#step-execution-model).
/// </para>
/// <para>
/// The generated PowerShell script is written to the step's artifacts directory
/// before execution so it appears as a downloadable artifact for troubleshooting.
/// </para>
/// </summary>
public sealed class OctopusWindowsServiceStepHandler(ScriptRunner scriptRunner) : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Octopus.WindowsService", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            await context.LogAsync("error",
                "Octopus.WindowsService steps can only run on Windows agents (the service control APIs are not available on this OS).")
                .ConfigureAwait(false);
            return false;
        }

        WindowsServiceConfig cfg;
        try
        {
            var octostache = BuildOctostache(context.Plan.Variables);
            cfg = WindowsServiceConfig.Parse(
                context.Step.Config,
                raw => octostache.Evaluate(raw),
                fallbackInstallRoot: context.ExtractDir);
        }
        catch (Exception ex)
        {
            await context.LogAsync("error",
                $"Invalid Octopus.WindowsService step configuration: {ex.Message}").ConfigureAwait(false);
            return false;
        }

        foreach (var w in cfg.Warnings)
        {
            await context.LogAsync("warning", w).ConfigureAwait(false);
        }

        await context.LogAsync("info",
            $"Octopus.WindowsService — service '{cfg.ServiceName}', account {cfg.ServiceAccount}, "
            + $"start mode {cfg.StartMode}, desired status {cfg.DesiredStatus}.")
            .ConfigureAwait(false);

        var script = WindowsServiceScriptGenerator.Generate(cfg, context.Plan.DeploymentId);

        try
        {
            Directory.CreateDirectory(context.ArtifactsDir);
            var scriptPath = Path.Combine(context.ArtifactsDir, "octopus-windowsservice.ps1");
            await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await context.LogAsync("warning",
                $"Could not write generated script to artifacts directory: {ex.Message}")
                .ConfigureAwait(false);
        }

        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["KRAKEN_ARTIFACTS_PATH"]       = context.ArtifactsDir,
            ["KRAKEN_DEPLOYMENT_ID"]        = context.Plan.DeploymentId.ToString(),
            ["KRAKEN_SERVICE_NAME"]         = cfg.ServiceName,
            ["KRAKEN_SERVICE_INSTALL_ROOT"] = cfg.InstallRoot,
        };

        return await scriptRunner.RunAsync(
            script,
            "PowerShell",
            context.ExtractDir,
            envVars,
            context.LogAsync,
            ct).ConfigureAwait(false);
    }

    private static VariableDictionary BuildOctostache(IReadOnlyDictionary<string, string> variables)
    {
        var dict = new VariableDictionary();
        foreach (var (k, v) in variables)
        {
            dict.Set(k, v);
        }
        return dict;
    }
}

using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.OctopusWindowsService;

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
public sealed class OctopusWindowsServiceStepHandler : IStepHandler
{
    // The agent's StepPackageLoader activates handlers via Activator; we
    // build a ScriptRunner on demand. ScriptRunner has no state to share.
    private readonly ScriptRunner _scriptRunner = new();

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
            // C5/T1-20: persist the downloadable .ps1 artifact with UTF-8-with-BOM
            // (same as the executed copy) so Windows PowerShell 5.1 reads Croatian.
            await File.WriteAllTextAsync(
                scriptPath, script, ScriptRunner.EncodingForSyntax("PowerShell"), ct)
                .ConfigureAwait(false);
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

        return await _scriptRunner.RunAsync(
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

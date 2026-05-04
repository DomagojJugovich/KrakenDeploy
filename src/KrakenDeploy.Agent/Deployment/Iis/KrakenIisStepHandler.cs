using KrakenDeploy.Agent.Deployment.StepHandlers;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Deployment.Iis;

/// <summary>
/// Handles the <c>Kraken.IIS</c> step type — a comprehensive superset of
/// <c>Octopus.IIS</c>. Windows-only.
/// <para>
/// Generates a single PowerShell script that:
/// <list type="bullet">
///   <item>Ensures and configures the app pool (process model, recycle, rapid-fail).</item>
///   <item>Ensures the site, replaces bindings (incl. SNI / cert from store).</item>
///   <item>Performs an in-place or atomic-swap deploy of the package contents.</item>
///   <item>Recycles the pool (drain-mode by default).</item>
///   <item>Optionally runs an HTTP health probe with retries.</item>
/// </list>
/// The generated script is written to the step's artifacts directory before execution
/// so it appears as a downloadable artifact for troubleshooting.
/// </para>
/// </summary>
public sealed class KrakenIisStepHandler(ScriptRunner scriptRunner) : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals("Kraken.IIS", StringComparison.OrdinalIgnoreCase)
        || stepType.Equals("Octopus.IIS", StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            await context.LogAsync("error",
                "Kraken.IIS steps can only run on Windows agents (IIS is not available on this OS).")
                .ConfigureAwait(false);
            return false;
        }

        KrakenIisConfig cfg;
        try
        {
            cfg = KrakenIisConfig.Parse(context.Step.Config);
        }
        catch (Exception ex)
        {
            await context.LogAsync("error",
                $"Invalid Kraken.IIS step configuration: {ex.Message}").ConfigureAwait(false);
            return false;
        }

        await context.LogAsync("info",
            $"Kraken.IIS — site '{cfg.SiteName}', app pool '{cfg.AppPool.Name}', " +
            $"deploy mode {cfg.Deploy.Mode}, {cfg.Bindings.Count} binding(s)" +
            (cfg.HealthCheck is null ? "." : ", health probe enabled."))
            .ConfigureAwait(false);

        // Generate the script and persist it as an artifact for troubleshooting.
        var script = IisScriptGenerator.Generate(cfg, context.ExtractDir, context.Plan.DeploymentId);

        try
        {
            Directory.CreateDirectory(context.ArtifactsDir);
            var scriptPath = Path.Combine(context.ArtifactsDir, "kraken-iis-deploy.ps1");
            await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await context.LogAsync("warning",
                $"Could not write generated script to artifacts directory: {ex.Message}")
                .ConfigureAwait(false);
        }

        // Run the script via the same runner used by Octopus.Script so log
        // streaming and exit-code handling are uniform across step types.
        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["KRAKEN_ARTIFACTS_PATH"] = context.ArtifactsDir,
            ["KRAKEN_DEPLOYMENT_ID"]  = context.Plan.DeploymentId.ToString(),
            ["KRAKEN_SITE_NAME"]      = cfg.SiteName,
            ["KRAKEN_APP_POOL"]       = cfg.AppPool.Name,
        };

        return await scriptRunner.RunAsync(
            script,
            "PowerShell",
            context.ExtractDir,
            envVars,
            context.LogAsync,
            ct).ConfigureAwait(false);
    }
}

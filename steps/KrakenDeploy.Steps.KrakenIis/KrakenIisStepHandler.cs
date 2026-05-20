using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Common;
using Octostache;

namespace KrakenDeploy.Steps.KrakenIis;

/// <summary>
/// Handles the <c>Kraken.IIS</c> step type — a comprehensive superset of
/// <c>Octopus.IIS</c> — and the imported <c>Octopus.IIS</c> shape (dual-shape
/// strategy, see TASKS.md Phase B-3). Windows-only.
/// <para>
/// On execution the handler picks a parser by inspecting the step's config:
/// presence of any <c>Octopus.Action.IISWebSite.*</c> key routes through
/// <see cref="OctopusIisConfig.MapToKrakenIisConfig"/> (which maps to
/// <see cref="KrakenIisConfig"/> internally); otherwise the Kraken-native
/// <see cref="KrakenIisConfig.Parse"/> path runs. Both shapes feed the same
/// <see cref="IisScriptGenerator"/>, so the PowerShell emit and run flow is
/// identical regardless of source.
/// </para>
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
public sealed class KrakenIisStepHandler : IStepHandler
{
    // The agent's StepPackageLoader instantiates handlers via Activator,
    // so we build a ScriptRunner on demand. ScriptRunner has no state worth
    // sharing across step executions.
    private readonly ScriptRunner _scriptRunner = new();

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

        string script;
        Dictionary<string, string> envVars;

        try
        {
            if (OctopusIisConfig.IsOctopusShape(context.Step.Config))
            {
                var octostache = BuildOctostache(context.Plan.Variables);
                var mapping = OctopusIisConfig.MapToKrakenIisConfig(
                    context.Step.Config,
                    raw => octostache.Evaluate(raw),
                    fallbackWebRoot: context.ExtractDir);
                foreach (var w in mapping.Warnings)
                {
                    await context.LogAsync("warning", w).ConfigureAwait(false);
                }

                if (mapping.WebSite is not null)
                {
                    (script, envVars) = await BuildSiteScriptAsync(
                        mapping.WebSite, context, ct).ConfigureAwait(false);
                }
                else if (mapping.WebApplication is not null)
                {
                    (script, envVars) = await BuildWebApplicationScriptAsync(
                        mapping.WebApplication, context, ct).ConfigureAwait(false);
                }
                else if (mapping.VirtualDirectory is not null)
                {
                    (script, envVars) = await BuildVirtualDirectoryScriptAsync(
                        mapping.VirtualDirectory, context, ct).ConfigureAwait(false);
                }
                else
                {
                    await context.LogAsync("error",
                        "Octopus.IIS mapper produced no result (this should not happen).")
                        .ConfigureAwait(false);
                    return false;
                }
            }
            else
            {
                var krakenCfg = KrakenIisConfig.Parse(context.Step.Config);
                (script, envVars) = await BuildSiteScriptAsync(
                    krakenCfg, context, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await context.LogAsync("error",
                $"Invalid Kraken.IIS step configuration: {ex.Message}").ConfigureAwait(false);
            return false;
        }

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

    private static async Task<(string Script, Dictionary<string, string> EnvVars)>
        BuildSiteScriptAsync(KrakenIisConfig cfg, StepHandlerContext context, CancellationToken ct)
    {
        await context.LogAsync("info",
            $"Kraken.IIS — site '{cfg.SiteName}', app pool '{cfg.AppPool.Name}', " +
            $"deploy mode {cfg.Deploy.Mode}, {cfg.Bindings.Count} binding(s)" +
            (cfg.HealthCheck is null ? "." : ", health probe enabled."))
            .ConfigureAwait(false);

        var script = IisScriptGenerator.Generate(cfg, context.ExtractDir, context.Plan.DeploymentId);
        await PersistArtifactAsync(script, "kraken-iis-deploy.ps1", context, ct).ConfigureAwait(false);

        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["KRAKEN_ARTIFACTS_PATH"] = context.ArtifactsDir,
            ["KRAKEN_DEPLOYMENT_ID"]  = context.Plan.DeploymentId.ToString(),
            ["KRAKEN_SITE_NAME"]      = cfg.SiteName,
            ["KRAKEN_APP_POOL"]       = cfg.AppPool.Name,
        };
        return (script, envVars);
    }

    private static async Task<(string Script, Dictionary<string, string> EnvVars)>
        BuildWebApplicationScriptAsync(
            KrakenIisWebApplicationConfig cfg, StepHandlerContext context, CancellationToken ct)
    {
        await context.LogAsync("info",
            $"Kraken.IIS web application — parent site '{cfg.ParentSiteName}', " +
            $"virtual path '{cfg.VirtualPath}', app pool '{cfg.AppPool.Name}'.")
            .ConfigureAwait(false);

        var script = IisScriptGenerator.GenerateWebApplication(
            cfg, context.ExtractDir, context.Plan.DeploymentId);
        await PersistArtifactAsync(script, "kraken-iis-webapplication.ps1", context, ct).ConfigureAwait(false);

        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["KRAKEN_ARTIFACTS_PATH"]    = context.ArtifactsDir,
            ["KRAKEN_DEPLOYMENT_ID"]     = context.Plan.DeploymentId.ToString(),
            ["KRAKEN_PARENT_SITE_NAME"]  = cfg.ParentSiteName,
            ["KRAKEN_VIRTUAL_PATH"]      = cfg.VirtualPath,
            ["KRAKEN_APP_POOL"]          = cfg.AppPool.Name,
        };
        return (script, envVars);
    }

    private static async Task<(string Script, Dictionary<string, string> EnvVars)>
        BuildVirtualDirectoryScriptAsync(
            KrakenIisVirtualDirectoryConfig cfg, StepHandlerContext context, CancellationToken ct)
    {
        await context.LogAsync("info",
            $"Kraken.IIS virtual directory — parent site '{cfg.ParentSiteName}', " +
            $"virtual path '{cfg.VirtualPath}'.")
            .ConfigureAwait(false);

        var script = IisScriptGenerator.GenerateVirtualDirectory(
            cfg, context.ExtractDir, context.Plan.DeploymentId);
        await PersistArtifactAsync(script, "kraken-iis-virtualdirectory.ps1", context, ct).ConfigureAwait(false);

        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["KRAKEN_ARTIFACTS_PATH"]    = context.ArtifactsDir,
            ["KRAKEN_DEPLOYMENT_ID"]     = context.Plan.DeploymentId.ToString(),
            ["KRAKEN_PARENT_SITE_NAME"]  = cfg.ParentSiteName,
            ["KRAKEN_VIRTUAL_PATH"]      = cfg.VirtualPath,
        };
        return (script, envVars);
    }

    private static async Task PersistArtifactAsync(
        string script, string fileName, StepHandlerContext context, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(context.ArtifactsDir);
            await File.WriteAllTextAsync(Path.Combine(context.ArtifactsDir, fileName), script, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await context.LogAsync("warning",
                $"Could not write generated script to artifacts directory: {ex.Message}")
                .ConfigureAwait(false);
        }
    }
}

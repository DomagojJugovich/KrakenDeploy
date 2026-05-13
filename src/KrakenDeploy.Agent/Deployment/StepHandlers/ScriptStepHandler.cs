using System.Text;

namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Handles <c>Kraken.Script</c> (Kraken-native) and <c>Octopus.Script</c>
/// (imported from Octopus). Both use the same Octopus-compatible config keys:
/// <c>Octopus.Action.Script.ScriptBody</c>, <c>Octopus.Action.Script.Syntax</c>,
/// and <c>Octopus.Action.PowerShell.Edition</c>.
/// <para>
/// Supported syntaxes: PowerShell (Desktop/Core), Bash, CSharp (dotnet-script),
/// FSharp (dotnet fsi), Python.
/// </para>
/// </summary>
public sealed class ScriptStepHandler(ScriptRunner scriptRunner) : IStepHandler
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Kraken.Script",
        "Octopus.Script",
    };

    public bool CanHandle(string stepType) => SupportedTypes.Contains(stepType);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var (scriptBody, scriptSyntax, psEdition) =
            ResolveScript(context.Step.Config, context.Step.StepType);

        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await context.LogAsync("error", "Step has no script body.").ConfigureAwait(false);
            return false;
        }

        // Build env vars for the child process.
        // Un-indexed Octopus.Action.* / Octopus.Step.* keys reference the
        // currently-executing step. The server emits indexed forms
        // (Octopus.Action[StepName].*) in plan.Variables — here we add the
        // un-indexed ones so scripts using the shorthand also resolve.
        var stepNumber = (context.Step.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["OctopusEnvironmentName"]      = context.Plan.EnvironmentName,
            ["OctopusPackageDirectoryPath"] = context.ExtractDir,
            ["KrakenDeploymentId"]          = context.Plan.DeploymentId.ToString(),
            ["KrakenStepName"]              = context.Step.Name,
            ["KrakenPackageId"]             = context.Step.PackageId,
            ["KrakenPackageVersion"]        = context.Step.PackageVersion,
            ["Octopus.Action.Name"]         = context.Step.Name,
            ["Octopus.Action.Id"]           = context.Step.Name,
            ["Octopus.Action.Number"]       = stepNumber,
            ["Octopus.Step.Name"]           = context.Step.Name,
            ["Octopus.Step.Number"]         = stepNumber,
            ["Octopus.Action.Package.PackageId"]      = context.Step.PackageId,
            ["Octopus.Action.Package.PackageVersion"] = context.Step.PackageVersion,
            ["Octopus.Action.Package.OriginalInstalledPath"] = context.ExtractDir,
            // Scripts write artifact files here; the executor uploads them after the step.
            ["KRAKEN_ARTIFACTS_PATH"]       = context.ArtifactsDir,
        };

        // The PowerShell preamble injects $OctopusParameters and Kraken helpers.
        // It only makes sense for PowerShell; other languages get variables via env.
        var isPowerShell = scriptSyntax.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);

        // Merge plan variables with the un-indexed action/step keys for this step
        // so $OctopusParameters["Octopus.Action.Name"] resolves inside the script.
        var preambleVars = new Dictionary<string, string>(context.Plan.Variables, StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Action.Name"]                          = context.Step.Name,
            ["Octopus.Action.Id"]                            = context.Step.Name,
            ["Octopus.Action.Number"]                        = stepNumber,
            ["Octopus.Step.Name"]                            = context.Step.Name,
            ["Octopus.Step.Number"]                          = stepNumber,
            ["Octopus.Action.Package.PackageId"]             = context.Step.PackageId,
            ["Octopus.Action.Package.PackageVersion"]        = context.Step.PackageVersion,
            ["Octopus.Action.Package.OriginalInstalledPath"] = context.ExtractDir,
        };

        var fullScript = isPowerShell
            ? BuildPowerShellPreamble(
                preambleVars,
                context.Plan.ArrayVariables,
                context.Plan.EnvironmentName,
                context.Plan.DeploymentId)
              + Environment.NewLine + Environment.NewLine
              + scriptBody
            : scriptBody;

        return await scriptRunner.RunAsync(
            fullScript,
            scriptSyntax,
            context.ExtractDir,
            envVars,
            context.LogAsync,
            ct,
            psEdition).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (string body, string syntax, string? psEdition) ResolveScript(
        IReadOnlyDictionary<string, string> config, string stepType)
    {
        _ = stepType; // Kraken.Script and Octopus.Script share the same key contract.
        config.TryGetValue("Octopus.Action.Script.ScriptBody", out var body);
        config.TryGetValue("Octopus.Action.Script.Syntax", out var syntax);
        config.TryGetValue("Octopus.Action.PowerShell.Edition", out var edition);
        return (body ?? string.Empty, syntax ?? "PowerShell", edition);
    }

    // ── PowerShell preamble ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a PowerShell preamble that populates <c>$OctopusParameters</c>,
    /// <c>$OctopusArrays</c>, and Kraken helper functions for back-compat with
    /// Octopus step templates.
    /// </summary>
    private static string BuildPowerShellPreamble(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string[]> arrayVariables,
        string environmentName,
        Guid deploymentId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ── KrakenDeploy: variable injection ──────────────────────────────────");

        // ── $OctopusParameters ─────────────────────────────────────────────────
        sb.AppendLine("$OctopusParameters = [ordered]@{");
        sb.Append("    'Octopus.Environment.Name' = '")
          .Append(EscapePs(environmentName)).AppendLine("'");
        sb.Append("    'Octopus.Deployment.Id'    = '")
          .Append(deploymentId.ToString()).AppendLine("'");

        foreach (var (name, value) in variables)
        {
            if (name.Equals("Octopus.Environment.Name", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Octopus.Deployment.Id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append("    '").Append(EscapePs(name)).Append("' = '")
              .Append(EscapePs(value)).AppendLine("'");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // ── $OctopusArrays ─────────────────────────────────────────────────────
        sb.AppendLine("$OctopusArrays = [ordered]@{");
        foreach (var (name, items) in arrayVariables)
        {
            var quotedItems = string.Join(", ", items.Select(v => $"'{EscapePs(v)}'"));
            sb.Append("    '").Append(EscapePs(name)).Append("' = @(")
              .Append(quotedItems).AppendLine(")");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // ── Kraken module functions ────────────────────────────────────────────
        sb.AppendLine("# KrakenDeploy PowerShell helpers");
        sb.AppendLine("function Write-KrakenInfo    { param([string]$Message) Write-Host $Message }");
        sb.AppendLine("function Write-KrakenWarning { param([string]$Message) Write-Warning $Message }");
        sb.AppendLine("function Write-KrakenError   { param([string]$Message) Write-Error $Message }");
        sb.AppendLine("function Get-KrakenVariable  { param([string]$Name) $OctopusParameters[$Name] }");
        // Register-KrakenArtifact: copies a file into the KRAKEN_ARTIFACTS_PATH directory
        // so the executor picks it up and streams it to the server.
        // The optional -Name parameter overrides the destination filename.
        sb.AppendLine("""
function Register-KrakenArtifact {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$Name = [System.IO.Path]::GetFileName($Path)
    )
    $dest = [System.IO.Path]::Combine($env:KRAKEN_ARTIFACTS_PATH, $Name)
    [System.IO.Directory]::CreateDirectory($env:KRAKEN_ARTIFACTS_PATH) | Out-Null
    Copy-Item -Path $Path -Destination $dest -Force
    Write-Host "[Artifact] Registered '$Name'"
}
""");
        sb.AppendLine("# Octopus-compat aliases");
        sb.AppendLine("Set-Alias -Name 'Write-Verbose' -Value 'Write-KrakenInfo' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("# ────────────────────────────────────────────────────────────────────");

        return sb.ToString();
    }

    private static string EscapePs(string s) => s.Replace("'", "''");
}

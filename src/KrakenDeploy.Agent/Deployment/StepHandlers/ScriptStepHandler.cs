using System.Text;

namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Handles <c>KrakenDeploy.Script</c> and <c>Octopus.Script</c> step types.
/// <para>
/// KrakenDeploy.Script config keys: <c>scriptBody</c>, <c>scriptSyntax</c>.
/// Octopus.Script config keys: <c>Octopus.Action.Script.ScriptBody</c>,
/// <c>Octopus.Action.Script.Syntax</c>.
/// </para>
/// </summary>
public sealed class ScriptStepHandler(ScriptRunner scriptRunner) : IStepHandler
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "KrakenDeploy.Script",
        "Octopus.Script",
    };

    public bool CanHandle(string stepType) => SupportedTypes.Contains(stepType);

    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var (scriptBody, scriptSyntax) = ResolveScript(context.Step.Config, context.Step.StepType);

        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await context.LogAsync("error", "Step has no script body.").ConfigureAwait(false);
            return false;
        }

        // Build env vars for the child process.
        var envVars = new Dictionary<string, string>(context.Plan.Variables)
        {
            ["OctopusEnvironmentName"]      = context.Plan.EnvironmentName,
            ["OctopusPackageDirectoryPath"] = context.ExtractDir,
            ["KrakenDeploymentId"]          = context.Plan.DeploymentId.ToString(),
            ["KrakenStepName"]              = context.Step.Name,
            ["KrakenPackageId"]             = context.Step.PackageId,
            ["KrakenPackageVersion"]        = context.Step.PackageVersion,
        };

        var isBash = scriptSyntax.Equals("Bash", StringComparison.OrdinalIgnoreCase);

        var fullScript = isBash
            ? scriptBody
            : BuildPowerShellPreamble(
                context.Plan.Variables,
                context.Plan.ArrayVariables,
                context.Plan.EnvironmentName,
                context.Plan.DeploymentId)
              + Environment.NewLine + Environment.NewLine
              + scriptBody;

        return await scriptRunner.RunAsync(
            fullScript,
            scriptSyntax,
            context.ExtractDir,
            envVars,
            context.LogAsync,
            ct).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (string body, string syntax) ResolveScript(
        IReadOnlyDictionary<string, string> config, string stepType)
    {
        if (stepType.Equals("Octopus.Script", StringComparison.OrdinalIgnoreCase))
        {
            config.TryGetValue("Octopus.Action.Script.ScriptBody", out var octBody);
            config.TryGetValue("Octopus.Action.Script.Syntax", out var octSyntax);
            return (octBody ?? string.Empty, octSyntax ?? "PowerShell");
        }

        // KrakenDeploy.Script — legacy keys.
        config.TryGetValue("scriptBody", out var body);
        config.TryGetValue("scriptSyntax", out var syntax);
        return (body ?? string.Empty, syntax ?? "PowerShell");
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
        sb.AppendLine("function Register-KrakenArtifact { param([string]$Path, [string]$Name) Write-Host \"[Artifact] $Path\" }");
        sb.AppendLine("# Octopus-compat aliases");
        sb.AppendLine("Set-Alias -Name 'Write-Verbose' -Value 'Write-KrakenInfo' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("# ────────────────────────────────────────────────────────────────────");

        return sb.ToString();
    }

    private static string EscapePs(string s) => s.Replace("'", "''");
}

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

        // Referenced packages — one env var per package + an indexed system
        // variable in $OctopusParameters (see preambleVars above).
        foreach (var (name, path) in context.ReferencedPackagePaths)
        {
            envVars[$"OCTOPUS_REFERENCED_PACKAGE_{name.ToUpperInvariant()}_PATH"] = path;
            envVars[$"Octopus.Action.Package[{name}].ExtractedPath"] = path;
        }

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

        // Referenced packages: each one gets an Octopus.Action.Package[Name].*
        // family of indexed system variables and a matching env var.
        foreach (var (name, path) in context.ReferencedPackagePaths)
        {
            preambleVars[$"Octopus.Action.Package[{name}].ExtractedPath"] = path;
        }

        // Each supported language gets a small preamble that exposes the same
        // surface as $OctopusParameters / Set-OctopusVariable / New-OctopusArtifact.
        // Variables are read from env at runtime (the agent already injects them
        // via envVars); the preamble just provides ergonomic accessors + the
        // helpers that emit the ##octopus[...] markers the agent parses.
        var preamble = scriptSyntax.ToLowerInvariant() switch
        {
            "powershell" => BuildPowerShellPreamble(
                preambleVars,
                context.Plan.ArrayVariables,
                context.Plan.EnvironmentName,
                context.Plan.DeploymentId),
            "bash"   => BuildBashPreamble(),
            "csharp" => BuildCSharpPreamble(),
            "fsharp" => BuildFSharpPreamble(),
            "python" => BuildPythonPreamble(),
            _        => string.Empty,
        };

        var fullScript = string.IsNullOrEmpty(preamble)
            ? scriptBody
            : preamble + Environment.NewLine + Environment.NewLine + scriptBody;

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
        // Set-OctopusVariable: emits a ##octopus[setVariable ...] marker on stdout
        // with base64-encoded name + value. The agent's DeploymentExecutor parses
        // these and accumulates output variables for the step. Subsequent steps in
        // the same deployment can read them via $OctopusParameters["Octopus.Action[StepName].Output.X"].
        // New-OctopusArtifact: Octopus-compatible alias that calls Register-KrakenArtifact.
        sb.AppendLine("""
function Set-OctopusVariable {
    param(
        [Parameter(Mandatory=$true)][string]$name,
        [Parameter(Mandatory=$true)][AllowEmptyString()][string]$value
    )
    $b64Name  = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($name))
    $b64Value = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))
    Write-Host "##octopus[setVariable name='$b64Name' value='$b64Value']"
}

function New-OctopusArtifact {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$Name = [System.IO.Path]::GetFileName($Path)
    )
    Register-KrakenArtifact -Path $Path -Name $Name
}
""");
        sb.AppendLine("# Octopus-compat aliases");
        sb.AppendLine("Set-Alias -Name 'Write-Verbose' -Value 'Write-KrakenInfo' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("# ────────────────────────────────────────────────────────────────────");

        return sb.ToString();
    }

    private static string EscapePs(string s) => s.Replace("'", "''");

    // ── Bash preamble ──────────────────────────────────────────────────────────

    private static string BuildBashPreamble() => """
# ── KrakenDeploy / Octopus compat helpers ─────────────────────────────────
# Octopus.* variables are already in the environment.
# Bash variable names can't contain dots, so use these helpers for access.

_kraken_b64() { printf '%s' "$1" | base64 | tr -d '\n'; }

get_octopusvariable() { printenv "$1"; }

set_octopusvariable() {
    local _name=$(_kraken_b64 "$1")
    local _value=$(_kraken_b64 "$2")
    echo "##octopus[setVariable name='${_name}' value='${_value}']"
}

new_octopusartifact() {
    local _path="$1"
    local _name="${2:-$(basename "$1")}"
    if [ -n "${KRAKEN_ARTIFACTS_PATH:-}" ]; then
        mkdir -p "$KRAKEN_ARTIFACTS_PATH"
        cp -f "$_path" "$KRAKEN_ARTIFACTS_PATH/$_name"
        echo "[Artifact] Registered '$_name'"
    fi
}
# ────────────────────────────────────────────────────────────────────────
""";

    // ── Python preamble ────────────────────────────────────────────────────────

    private static string BuildPythonPreamble() => """
# ── KrakenDeploy / Octopus compat helpers ─────────────────────────────────
import os as _kraken_os
import base64 as _kraken_base64
import shutil as _kraken_shutil

octopusvariables = {k: v for k, v in _kraken_os.environ.items() if k.startswith("Octopus.")}
OctopusParameters = octopusvariables  # Octopus / C# naming alias

def get_octopusvariable(name):
    return _kraken_os.environ.get(name, "")

def set_octopusvariable(name, value):
    b64_name = _kraken_base64.b64encode(str(name).encode("utf-8")).decode("ascii")
    b64_value = _kraken_base64.b64encode(str(value).encode("utf-8")).decode("ascii")
    print(f"##octopus[setVariable name='{b64_name}' value='{b64_value}']", flush=True)

def new_octopusartifact(path, name=None):
    name = name or _kraken_os.path.basename(path)
    artifacts = _kraken_os.environ.get("KRAKEN_ARTIFACTS_PATH")
    if artifacts:
        _kraken_os.makedirs(artifacts, exist_ok=True)
        _kraken_shutil.copyfile(path, _kraken_os.path.join(artifacts, name))
        print(f"[Artifact] Registered '{name}'", flush=True)
# ────────────────────────────────────────────────────────────────────────
""";

    // ── C# (dotnet-script) preamble ────────────────────────────────────────────

    private static string BuildCSharpPreamble() => """
// ── KrakenDeploy / Octopus compat helpers ─────────────────────────────────
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

var OctopusParameters = Environment.GetEnvironmentVariables()
    .Cast<DictionaryEntry>()
    .Where(e => e.Key.ToString()!.StartsWith("Octopus.", StringComparison.Ordinal))
    .ToDictionary(
        e => e.Key.ToString()!,
        e => e.Value?.ToString() ?? "",
        StringComparer.OrdinalIgnoreCase);

string GetOctopusVariable(string name) =>
    Environment.GetEnvironmentVariable(name) ?? "";

void SetOctopusVariable(string name, string value)
{
    var b64Name  = Convert.ToBase64String(Encoding.UTF8.GetBytes(name ?? ""));
    var b64Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
    Console.WriteLine($"##octopus[setVariable name='{b64Name}' value='{b64Value}']");
}

void NewOctopusArtifact(string path, string? name = null)
{
    name ??= Path.GetFileName(path);
    var dir = Environment.GetEnvironmentVariable("KRAKEN_ARTIFACTS_PATH");
    if (!string.IsNullOrEmpty(dir))
    {
        Directory.CreateDirectory(dir);
        File.Copy(path, Path.Combine(dir, name), overwrite: true);
        Console.WriteLine($"[Artifact] Registered '{name}'");
    }
}
// ────────────────────────────────────────────────────────────────────────
""";

    // ── F# (dotnet fsi) preamble ───────────────────────────────────────────────

    private static string BuildFSharpPreamble() => """
// ── KrakenDeploy / Octopus compat helpers ─────────────────────────────────
open System
open System.Collections
open System.IO
open System.Text

let OctopusParameters =
    Environment.GetEnvironmentVariables()
    |> Seq.cast<DictionaryEntry>
    |> Seq.filter (fun e -> (string e.Key).StartsWith("Octopus."))
    |> Seq.map (fun e -> string e.Key, (if isNull e.Value then "" else string e.Value))
    |> Map.ofSeq

let getOctopusVariable (name: string) =
    let v = Environment.GetEnvironmentVariable(name)
    if isNull v then "" else v

let setOctopusVariable (name: string) (value: string) =
    let safeName  = if isNull name then "" else name
    let safeValue = if isNull value then "" else value
    let b64Name  = Convert.ToBase64String(Encoding.UTF8.GetBytes(safeName))
    let b64Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(safeValue))
    printfn "##octopus[setVariable name='%s' value='%s']" b64Name b64Value

let newOctopusArtifact (path: string) (name: string) =
    let actualName = if String.IsNullOrEmpty(name) then Path.GetFileName(path) else name
    let dir = Environment.GetEnvironmentVariable("KRAKEN_ARTIFACTS_PATH")
    if not (String.IsNullOrEmpty(dir)) then
        Directory.CreateDirectory(dir) |> ignore
        File.Copy(path, Path.Combine(dir, actualName), true)
        printfn "[Artifact] Registered '%s'" actualName
// ────────────────────────────────────────────────────────────────────────
""";
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Executes <c>Octopus.Action.RunOnServer = "true"</c> script steps in the
/// server process instead of dispatching them to an agent. Mirrors the
/// agent-side <c>ScriptRunner</c> for the supported syntaxes (PowerShell
/// Desktop/Core, Bash), but writes log lines directly to the
/// <see cref="DeploymentLogEntry"/> table and broadcasts them to the live UI
/// via <see cref="UiHub"/> — the same surface the agent path uses.
/// <para>
/// Non-PowerShell-or-Bash syntaxes (CSharp / FSharp / Python) currently fall
/// back to running via the syntax's interpreter on the server if available;
/// callers should prefer agent execution for those.
/// </para>
/// </summary>
public sealed class ServerScriptStepRunner(
    IServiceScopeFactory scopeFactory,
    IHubContext<UiHub, IUiHubClient> uiHub,
    TimeProvider timeProvider,
    ILogger<ServerScriptStepRunner> logger)
{
    /// <summary>
    /// Runs <paramref name="step"/> in the server process. Returns
    /// <c>true</c> on exit code 0. Logs are streamed to the deployment's log
    /// and to the live-log UI hub.
    /// </summary>
    public async Task<bool> ExecuteAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        IReadOnlyDictionary<string, string> planVariables,
        CancellationToken ct)
    {
        await AppendLogAsync(deploymentId, "info",
            $"--- Step {step.Index + 1}: {step.Name} (server-side) ---", ct).ConfigureAwait(false);

        var scriptBody = step.Config.TryGetValue("Octopus.Action.Script.ScriptBody", out var b) ? b : "";
        var syntax     = step.Config.TryGetValue("Octopus.Action.Script.Syntax", out var s) ? s : "PowerShell";
        var psEdition  = step.Config.TryGetValue("Octopus.Action.PowerShell.Edition", out var e) ? e : null;

        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await AppendLogAsync(deploymentId, "error",
                "Step has no script body.", ct).ConfigureAwait(false);
            return false;
        }

        // Env vars: plan variables + current-step keys (mirror of agent's
        // ScriptStepHandler so script logic that reads $OctopusParameters
        // works server-side too).
        var stepNumber = (step.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var envVars = new Dictionary<string, string>(planVariables, StringComparer.OrdinalIgnoreCase)
        {
            ["OctopusEnvironmentName"]               = planVariables.TryGetValue("Octopus.Environment.Name", out var en) ? en : "",
            ["KrakenDeploymentId"]                   = deploymentId.ToString(),
            ["KrakenStepName"]                       = step.Name,
            ["Octopus.Action.Name"]                  = step.Name,
            ["Octopus.Action.Id"]                    = step.Name,
            ["Octopus.Action.Number"]                = stepNumber,
            ["Octopus.Step.Name"]                    = step.Name,
            ["Octopus.Step.Number"]                  = stepNumber,
            ["Octopus.Action.RunOnServer"]           = "true",
        };

        // Build the script + preamble (PowerShell only — Bash uses env vars
        // directly, other syntaxes are pass-through).
        var isPowerShell = syntax.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);
        var fullScript = isPowerShell
            ? BuildPowerShellPreamble(envVars) + Environment.NewLine + Environment.NewLine + scriptBody
            : scriptBody;

        var scriptFile = WriteScriptFile(fullScript, syntax);
        try
        {
            var (exe, args) = BuildCommand(scriptFile, syntax, psEdition);
            logger.LogDebug(
                "Running server-side script for deployment {Id} step '{Step}': {Exe} {Args}",
                deploymentId, step.Name, exe, args);

            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                WorkingDirectory       = Path.GetTempPath(),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            foreach (var (k, v) in envVars)
            {
                psi.Environment[k] = v;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var pending = new List<Task>();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    pending.Add(AppendLogAsync(deploymentId, "info", e.Data, ct));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    pending.Add(AppendLogAsync(deploymentId, "error", e.Data, ct));
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(pending).ConfigureAwait(false);

            var success = process.ExitCode == 0;
            await AppendLogAsync(deploymentId,
                success ? "info" : "error",
                success ? $"Step '{step.Name}' succeeded." : $"Step '{step.Name}' failed (exit {process.ExitCode}).",
                ct).ConfigureAwait(false);
            return success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Server-side script step '{Step}' for deployment {Id} crashed.",
                step.Name, deploymentId);
            await AppendLogAsync(deploymentId, "error",
                $"Server-side execution crashed: {ex.Message}", ct).ConfigureAwait(false);
            return false;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { /* best effort */ }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string WriteScriptFile(string body, string syntax)
    {
        var ext = syntax.ToLowerInvariant() switch
        {
            "bash"   => ".sh",
            "csharp" => ".csx",
            "fsharp" => ".fsx",
            "python" => ".py",
            _        => ".ps1",
        };
        var path = Path.Combine(Path.GetTempPath(), $"kraken-server-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, body);
        return path;
    }

    private static (string exe, string args) BuildCommand(
        string scriptFile, string syntax, string? powerShellEdition)
    {
        switch (syntax.ToLowerInvariant())
        {
            case "bash":   return ("bash",   $"\"{scriptFile}\"");
            case "csharp": return ("dotnet", $"script \"{scriptFile}\"");
            case "fsharp": return ("dotnet", $"fsi \"{scriptFile}\"");
            case "python": return ("python", $"\"{scriptFile}\"");
            default:
                var wantDesktop = "Desktop".Equals(
                    powerShellEdition, StringComparison.OrdinalIgnoreCase);
                if (wantDesktop && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return ("powershell.exe",
                        $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"");
                }
                return ("pwsh", $"-NonInteractive -NoProfile -File \"{scriptFile}\"");
        }
    }

    private static string BuildPowerShellPreamble(IReadOnlyDictionary<string, string> variables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ── KrakenDeploy (server-side): variable injection ─────────────────────");
        sb.AppendLine("$OctopusParameters = [ordered]@{");
        foreach (var (name, value) in variables)
        {
            sb.Append("    '").Append(EscapePs(name)).Append("' = '")
              .Append(EscapePs(value)).AppendLine("'");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function Write-KrakenInfo    { param([string]$Message) Write-Host $Message }");
        sb.AppendLine("function Write-KrakenWarning { param([string]$Message) Write-Warning $Message }");
        sb.AppendLine("function Write-KrakenError   { param([string]$Message) Write-Error $Message }");
        sb.AppendLine("function Get-KrakenVariable  { param([string]$Name) $OctopusParameters[$Name] }");
        sb.AppendLine("function Set-OctopusVariable {");
        sb.AppendLine("    param([string]$name, [AllowEmptyString()][string]$value)");
        sb.AppendLine("    $b64n = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($name))");
        sb.AppendLine("    $b64v = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))");
        sb.AppendLine("    Write-Host \"##octopus[setVariable name='$b64n' value='$b64v']\"");
        sb.AppendLine("}");
        sb.AppendLine("# ─────────────────────────────────────────────────────────────────────");
        return sb.ToString();
    }

    private static string EscapePs(string s) => s.Replace("'", "''");

    /// <summary>
    /// Writes a log entry to <c>deployment_log_entries</c> and broadcasts it
    /// over <see cref="UiHub"/> — same contract as <c>AgentHub.AppendLogAsync</c>
    /// so the deployment-detail page renders server-side lines exactly like
    /// agent lines.
    /// </summary>
    private async Task AppendLogAsync(
        Guid deploymentId, string level, string message, CancellationToken ct)
    {
        var timestamp = timeProvider.GetUtcNow();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var deployment = await db.Deployments.FindAsync([deploymentId], ct).ConfigureAwait(false);
        if (deployment is null)
        {
            logger.LogWarning("ServerScriptStepRunner: deployment {Id} not found for log line.", deploymentId);
            return;
        }

        var seq = deployment.NextLogSequence++;
        db.DeploymentLogEntries.Add(new DeploymentLogEntry
        {
            DeploymentId = deploymentId,
            Sequence     = seq,
            Timestamp    = timestamp,
            Message      = message,
            Level        = level,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await uiHub.Clients.Group($"deployment:{deploymentId}")
            .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
            .ConfigureAwait(false);
    }
}

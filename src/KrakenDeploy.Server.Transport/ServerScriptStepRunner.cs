using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// One server-side script execution's result: exit-code success plus the
/// output variables captured from <c>##octopus[setVariable]</c> stdout
/// markers (B4/T1-6) with their sensitive subset (T0-6). Failure paths carry
/// empty captures.
/// </summary>
public sealed record ServerScriptResult(
    bool Success,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyCollection<string> SensitiveOutputNames)
{
    public static ServerScriptResult Failure { get; } = new(
        false,
        new Dictionary<string, string>(),
        []);
}

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
    /// Runs <paramref name="step"/> in the server process. Success = exit
    /// code 0. Logs are streamed to the deployment's log and to the live-log
    /// UI hub. B4/T1-6: <c>##octopus[setVariable]</c> markers in stdout are
    /// captured as output variables via the shared
    /// <see cref="OctopusMessageParser"/> — exactly like the agent — and the
    /// marker line itself is CONSUMED, never logged (pre-B4 the raw marker,
    /// including the base64 of a sensitive value, landed in the task log).
    /// </summary>
    public async Task<ServerScriptResult> ExecuteAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        IReadOnlyDictionary<string, string> planVariables,
        SecretRedactor redactor,
        CancellationToken ct)
    {
        await AppendLogAsync(deploymentId, step.Index, "info",
            $"--- Step {step.Index + 1}: {step.Name} (server-side) ---", redactor, ct).ConfigureAwait(false);

        var scriptBody = step.Config.TryGetValue("Octopus.Action.Script.ScriptBody", out var b) ? b : "";
        var syntax     = step.Config.TryGetValue("Octopus.Action.Script.Syntax", out var s) ? s : "PowerShell";
        var psEdition  = step.Config.TryGetValue("Octopus.Action.PowerShell.Edition", out var e) ? e : null;

        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await AppendLogAsync(deploymentId, step.Index, "error",
                "Step has no script body.", redactor, ct).ConfigureAwait(false);
            return ServerScriptResult.Failure;
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
        var workDir = Path.GetDirectoryName(scriptFile)!;
        try
        {
            var (exe, args) = BuildCommand(
                scriptFile, syntax, psEdition,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            logger.LogDebug(
                "Running server-side script for deployment {Id} step '{Step}': {Exe} {Args}",
                deploymentId, step.Name, exe, args);

            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                WorkingDirectory       = workDir,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                // C5/T1-20: decode the child's output as UTF-8 (Croatian survives).
                // The PowerShell preamble forces the child to EMIT UTF-8 too.
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding  = Utf8NoBom,
            };
            foreach (var (k, v) in envVars)
            {
                psi.Environment[k] = v;
            }

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // stdout + stderr callbacks fire on separate threadpool threads;
            // List<T>.Add is not thread-safe, so every Add takes this lock
            // (pre-existing race, surfaced by the B4 review).
            var pending = new List<Task>();
            var pendingGate = new object();

            // B4 — captured output variables. OutputDataReceived events for one
            // stream are raised serially, so the dict/list/sticky-level need no
            // locking; stderr never carries markers and stays a plain pipe.
            var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sensitiveNames = new List<string>();
            var stickyLevel = "info";

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                switch (OctopusMessageParser.TryParse(e.Data))
                {
                    case SetVariableMessage v:
                        captured[v.Name] = v.Value;
                        if (v.Sensitive)
                        {
                            if (!sensitiveNames.Contains(v.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                sensitiveNames.Add(v.Name);
                            }
                            // Mask the value in every SUBSEQUENT line immediately
                            // (agent parity — T0-6 live fold).
                            if (v.Value.Length > 0)
                            {
                                redactor.Add([v.Value]);
                            }
                        }
                        return; // marker consumed — never logged
                    case SetLogLevelMessage l:
                        stickyLevel = l.Level;
                        return;
                    case CreateArtifactMessage a:
                        // Server-side steps have no artifact collection dir; the
                        // marker is surfaced informationally (agent parity).
                        lock (pendingGate)
                        {
                            pending.Add(AppendLogAsync(deploymentId, step.Index, "info",
                                $"[Artifact] {a.Name} ({a.Path})", redactor, ct));
                        }
                        return;
                    case ProgressMessage p:
                        lock (pendingGate)
                        {
                            pending.Add(AppendLogAsync(deploymentId, step.Index, "info",
                                $"[Progress {p.Percentage}%] {p.Message}", redactor, ct));
                        }
                        return;
                    // UnknownMessage / null: plain log line — fall through.
                }

                lock (pendingGate)
                {
                    pending.Add(AppendLogAsync(deploymentId, step.Index, stickyLevel, e.Data, redactor, ct));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (pendingGate)
                    {
                        pending.Add(AppendLogAsync(deploymentId, step.Index, "error", e.Data, redactor, ct));
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // B7: kill the spawned process TREE — WaitForExitAsync only stops
                // WAITING, so every per-step timeout and deployment cancel used to
                // leak an orphan process that kept running (and kept mutating
                // server-side state). Mirrors the agent's ScriptRunner kill (B6),
                // with the same bounded reap. Rethrown for the outer catch to
                // classify (timeout vs cancel).
                try
                {
                    process.Kill(entireProcessTree: true);
                    logger.LogWarning(
                        "Server-side script step '{Step}' for deployment {Id}: process tree " +
                        "killed (timed out or cancelled).",
                        step.Name, deploymentId);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the cancellation and the kill.
                }
                catch (Exception killEx)
                {
                    logger.LogWarning(killEx,
                        "Failed to kill the server-side script process tree for step '{Step}' " +
                        "of deployment {Id}.", step.Name, deploymentId);
                }
                try
                {
                    using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(reap.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "Server-side script process for step '{Step}' of deployment {Id} did " +
                        "not exit within 10 s of the kill.", step.Name, deploymentId);
                }
                throw;
            }
            // Streams are closed once WaitForExit completes and the final null
            // callbacks fired; snapshot under the gate for a consistent view.
            Task[] pendingSnapshot;
            lock (pendingGate)
            {
                pendingSnapshot = pending.ToArray();
            }
            await Task.WhenAll(pendingSnapshot).ConfigureAwait(false);

            var success = process.ExitCode == 0;
            await AppendLogAsync(deploymentId, step.Index,
                success ? "info" : "error",
                success ? $"Step '{step.Name}' succeeded." : $"Step '{step.Name}' failed (exit {process.ExitCode}).",
                redactor, ct).ConfigureAwait(false);
            return new ServerScriptResult(success, captured, sensitiveNames);
        }
        catch (OperationCanceledException)
        {
            // The per-attempt timeout (StepRetryRunner's linked CancelAfter) and a
            // deployment-level cancel both surface here as WaitForExitAsync throwing.
            // Propagate so StepRetryRunner can classify it: a per-step timeout becomes
            // StepOutcomeKind.TimedOut (the generic catch below would otherwise mis-
            // report it as Failed), and a deployment cancel propagates as cancellation.
            // B7: the spawned process tree was already killed + reaped at the wait
            // site above — no orphan survives this path anymore.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Server-side script step '{Step}' for deployment {Id} crashed.",
                step.Name, deploymentId);
            await AppendLogAsync(deploymentId, step.Index, "error",
                $"Server-side execution crashed: {ex.Message}", redactor, ct).ConfigureAwait(false);
            return ServerScriptResult.Failure;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // C5/T1-20: PowerShell scripts are written UTF-8 WITH a BOM so Windows
    // PowerShell 5.1 (Desktop) reads them as UTF-8 rather than the system ANSI
    // code page (Croatian č ć š ž đ would otherwise be corrupted). Other syntaxes
    // stay BOM-LESS (a BOM breaks a bash shebang). Mirrors Steps.Common.ScriptRunner.
    private static readonly UTF8Encoding Utf8Bom   = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static Encoding EncodingForSyntax(string syntax) =>
        syntax.ToLowerInvariant() is "bash" or "csharp" or "fsharp" or "python"
            ? Utf8NoBom : Utf8Bom;

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
        var workDir = Directory.CreateTempSubdirectory("kraken-server-script-").FullName;
        var path = Path.Combine(workDir, $"script{ext}");
        File.WriteAllText(path, body, EncodingForSyntax(syntax));
        return path;
    }

    internal static (string exe, string args) BuildCommand(
        string scriptFile, string syntax, string? powerShellEdition, bool isWindows)
    {
        switch (syntax.ToLowerInvariant())
        {
            case "bash":   return ("bash",   $"\"{scriptFile}\"");
            case "csharp": return ("dotnet", $"script \"{scriptFile}\"");
            case "fsharp": return ("dotnet", $"fsi \"{scriptFile}\"");
            case "python": return ("python", $"\"{scriptFile}\"");
            default:
                // C5/T1-20: default to Windows PowerShell (Desktop) on Windows
                // (pwsh/Core is not on stock Windows Server); only an explicit
                // "Core" edition selects pwsh. Off Windows, everything runs pwsh.
                // Mirrors Steps.Common.ScriptRunner.BuildCommand.
                var wantCore = "Core".Equals(
                    powerShellEdition?.Trim(), StringComparison.OrdinalIgnoreCase);
                if (isWindows && !wantCore)
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
        // C5/T1-20: emit UTF-8 so Croatian (č ć š ž đ) in output survives. Windows
        // PowerShell 5.1 otherwise emits the OEM code page; pwsh already emits
        // UTF-8. Wrapped: [Console]::OutputEncoding throws when no console is
        // attached. Paired with the parent's StandardOutputEncoding = UTF-8.
        sb.AppendLine("try { $OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }");
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
        sb.AppendLine("    param([string]$name, [AllowEmptyString()][string]$value, [switch]$sensitive)");
        sb.AppendLine("    $b64n = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($name))");
        sb.AppendLine("    $b64v = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))");
        sb.AppendLine("    if ($sensitive) {");
        sb.AppendLine("        Write-Host \"##octopus[setVariable name='$b64n' value='$b64v' sensitive='True']\"");
        sb.AppendLine("    } else {");
        sb.AppendLine("        Write-Host \"##octopus[setVariable name='$b64n' value='$b64v']\"");
        sb.AppendLine("    }");
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
        Guid deploymentId, int stepIndex, string level, string message,
        SecretRedactor redactor, CancellationToken ct)
    {
        // T0-6: single chokepoint — mask known sensitive values before the line
        // is persisted (TaskLogService) or broadcast (UiHub). No call site can
        // bypass this. No-op when the plan carries no sensitive variables.
        message = redactor.Redact(message);

        var timestamp = timeProvider.GetUtcNow();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        // Background scope → DefaultSpaceId; confirm the task exists filter-free
        // (an already-authorised by-id read). Server-side steps aren't bound to a
        // target, so the line's target_id is null.
        var exists = await db.ServerTasks.IgnoreQueryFilters()
            .AnyAsync(t => t.Id == deploymentId, ct).ConfigureAwait(false);
        if (!exists)
        {
            logger.LogWarning("ServerScriptStepRunner: task {Id} not found for log line.", deploymentId);
            return;
        }

        // Route through the SHARED DB-atomic sequencer — the same one the agent
        // path uses — so parallel server-side wave steps can't take duplicate
        // sequence numbers (closes the old unguarded NextLogSequence++ race).
        var seq = await TaskLogService.AppendLiveAsync(
            db, deploymentId, stepIndex, targetId: null, level, message, timestamp, ct)
            .ConfigureAwait(false);

        await uiHub.Clients.Group($"deployment:{deploymentId}")
            .DeploymentLogAppendedAsync(deploymentId, seq, timestamp, level, message)
            .ConfigureAwait(false);
    }
}

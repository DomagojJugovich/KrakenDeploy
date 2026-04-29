using System.Globalization;
using System.Text;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Executes a <see cref="DeploymentPlan"/> received from the server.
/// Downloads each step's package via gRPC, extracts it, runs the script,
/// streams log lines back via SignalR, and signals completion.
/// <para>
/// Before invoking the user script, a PowerShell preamble is injected that
/// populates <c>$OctopusParameters</c> (all scalar variables) and
/// <c>$OctopusArrays</c> (StringArray variables as native arrays).
/// Bash scripts receive variables via environment variables.
/// </para>
/// </summary>
public sealed class DeploymentExecutor(
    AgentContext context,
    IServerLink serverLink,
    GrpcPackageDownloader packageDownloader,
    ScriptRunner scriptRunner,
    IOptions<AgentConfig> agentConfig,
    ILogger<DeploymentExecutor> logger)
{
    public async Task ExecuteAsync(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        logger.LogInformation(
            "Starting deployment {DeploymentId} ({StepCount} step(s)) in environment {Env}.",
            plan.DeploymentId, plan.Steps.Length, plan.EnvironmentName);

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        try
        {
            foreach (var step in plan.Steps.OrderBy(s => s.Index))
            {
                var success = await ExecuteStepAsync(plan, step, ct).ConfigureAwait(false);
                if (!success)
                {
                    await serverLink
                        .CompleteDeploymentAsync(plan.DeploymentId, false,
                            $"Step '{step.Name}' failed.", ct)
                        .ConfigureAwait(false);
                    return;
                }
            }

            await serverLink
                .CompleteDeploymentAsync(plan.DeploymentId, true, null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled error executing deployment {DeploymentId}.", plan.DeploymentId);
            try
            {
                await serverLink
                    .CompleteDeploymentAsync(plan.DeploymentId, false, ex.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                logger.LogError(inner,
                    "Failed to report deployment failure for {DeploymentId}.", plan.DeploymentId);
            }
        }
    }

    // ── Step execution ─────────────────────────────────────────────────────

    private async Task<bool> ExecuteStepAsync(
        DeploymentPlan plan, DeploymentStepPlan step, CancellationToken ct)
    {
        await LogAsync(plan.DeploymentId, "info",
            $"--- Step {step.Index + 1}: {step.Name} ---", ct).ConfigureAwait(false);

        // ── 1. Download package ────────────────────────────────────────────
        var tempRoot = Path.Combine(
            agentConfig.Value.ResolvedDataPath, "staging",
            plan.DeploymentId.ToString("N"),
            step.Index.ToString(CultureInfo.InvariantCulture));

        Directory.CreateDirectory(tempRoot);

        string zipPath;
        try
        {
            await LogAsync(plan.DeploymentId, "info",
                $"Downloading {step.PackageId} v{step.PackageVersion}…", ct)
                .ConfigureAwait(false);

            var identity = context.Identity!;
            zipPath = await packageDownloader
                .DownloadAsync(identity.ServerUrl, identity.AgentToken,
                    step.PackageId, step.PackageVersion, tempRoot, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Package download failed: {ex.Message}", ct).ConfigureAwait(false);
            return false;
        }

        // ── 2. Extract ─────────────────────────────────────────────────────
        var extractDir = Path.Combine(tempRoot, "extracted");
        try
        {
            await LogAsync(plan.DeploymentId, "info", "Extracting package…", ct)
                .ConfigureAwait(false);

            await PackageExtractor.ExtractAsync(zipPath, extractDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Package extraction failed: {ex.Message}", ct).ConfigureAwait(false);
            return false;
        }

        // ── 3. Run script ──────────────────────────────────────────────────
        if (!step.StepType.Equals("KrakenDeploy.Script", StringComparison.OrdinalIgnoreCase))
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Unknown step type '{step.StepType}'.", ct).ConfigureAwait(false);
            return false;
        }

        step.Config.TryGetValue("scriptBody", out var scriptBody);
        step.Config.TryGetValue("scriptSyntax", out var scriptSyntax);

        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            await LogAsync(plan.DeploymentId, "error",
                "Step has no script body.", ct).ConfigureAwait(false);
            return false;
        }

        // Environment variables injected for all script types.
        var envVars = new Dictionary<string, string>(plan.Variables)
        {
            ["OctopusEnvironmentName"]      = plan.EnvironmentName,
            ["OctopusPackageDirectoryPath"] = extractDir,
            ["KrakenDeploymentId"]          = plan.DeploymentId.ToString(),
            ["KrakenStepName"]              = step.Name,
            ["KrakenPackageId"]             = step.PackageId,
            ["KrakenPackageVersion"]        = step.PackageVersion,
        };

        var isBash = scriptSyntax?.Equals("Bash", StringComparison.OrdinalIgnoreCase) == true;

        // For PowerShell: prepend the $OctopusParameters / $OctopusArrays preamble.
        // For Bash: variables are already available as env vars (set above).
        var fullScript = isBash
            ? scriptBody
            : BuildPowerShellPreamble(plan.Variables, plan.ArrayVariables,
                plan.EnvironmentName, plan.DeploymentId)
              + Environment.NewLine + Environment.NewLine
              + scriptBody;

        var success = await scriptRunner.RunAsync(
            fullScript,
            scriptSyntax ?? "PowerShell",
            extractDir,
            envVars,
            async (level, message) =>
                await LogAsync(plan.DeploymentId, level, message, ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        await LogAsync(plan.DeploymentId, success ? "info" : "error",
            success ? $"Step '{step.Name}' succeeded." : $"Step '{step.Name}' failed.",
            ct).ConfigureAwait(false);

        // ── 4. Cleanup staging ─────────────────────────────────────────────
        try { Directory.Delete(tempRoot, recursive: true); }
        catch { /* non-fatal */ }

        return success;
    }

    // ── PowerShell preamble ────────────────────────────────────────────────

    /// <summary>
    /// Builds a PowerShell preamble that populates:
    /// <list type="bullet">
    ///   <item><c>$OctopusParameters</c> — all scalar variables (Octopus back-compat)</item>
    ///   <item><c>$OctopusArrays</c> — StringArray variables as native PS arrays</item>
    /// </list>
    /// </summary>
    private static string BuildPowerShellPreamble(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, string[]> arrayVariables,
        string environmentName,
        Guid deploymentId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ── KrakenDeploy: variable injection ──────────────────────────────────");
        sb.AppendLine("$OctopusParameters = [ordered]@{");

        // Standard Octopus variables.
        sb.Append("    'Octopus.Environment.Name' = '")
          .Append(EscapePs(environmentName))
          .AppendLine("'");
        sb.Append("    'Octopus.Deployment.Id'    = '")
          .Append(deploymentId.ToString())
          .AppendLine("'");

        foreach (var (name, value) in variables)
        {
            // Skip the standard vars already added above.
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

        sb.AppendLine("$OctopusArrays = [ordered]@{");
        foreach (var (name, items) in arrayVariables)
        {
            var quotedItems = string.Join(", ", items.Select(v => $"'{EscapePs(v)}'"));
            sb.Append("    '").Append(EscapePs(name)).Append("' = @(")
              .Append(quotedItems).AppendLine(")");
        }

        sb.AppendLine("}");
        sb.AppendLine("# ────────────────────────────────────────────────────────────────────");

        return sb.ToString();
    }

    /// <summary>Escapes a string for use inside single-quoted PowerShell strings.</summary>
    private static string EscapePs(string s) => s.Replace("'", "''");

    // ── Logging helper ─────────────────────────────────────────────────────

    private async Task LogAsync(
        Guid deploymentId, string level, string message, CancellationToken ct)
    {
        logger.LogDebug("[Deployment {Id}] {Level}: {Message}", deploymentId, level, message);
        try
        {
            await serverLink.AppendLogAsync(deploymentId, level, message, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to send log line to server for deployment {Id}.", deploymentId);
        }
    }
}

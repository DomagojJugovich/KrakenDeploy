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
            step.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

        var envVars = new Dictionary<string, string>(plan.Variables)
        {
            ["OctopusEnvironmentName"]     = plan.EnvironmentName,
            ["OctopusPackageDirectoryPath"] = extractDir,
            ["KrakenDeploymentId"]          = plan.DeploymentId.ToString(),
            ["KrakenStepName"]              = step.Name,
            ["KrakenPackageId"]             = step.PackageId,
            ["KrakenPackageVersion"]        = step.PackageVersion,
        };

        var success = await scriptRunner.RunAsync(
            scriptBody,
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

    // ── Helpers ────────────────────────────────────────────────────────────

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

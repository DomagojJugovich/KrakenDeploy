using System.Globalization;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment.StepHandlers;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Executes a <see cref="DeploymentPlan"/> received from the server.
/// For each step the executor:
/// <list type="number">
///   <item>Resolves the first registered <see cref="IStepHandler"/> that can handle the step type.</item>
///   <item>Optionally downloads and extracts the step's package (if the handler requires it).</item>
///   <item>Delegates execution to the handler.</item>
///   <item>Streams log lines back via <see cref="IServerLink"/>.</item>
///   <item>Signals completion to the server.</item>
/// </list>
/// </summary>
public sealed class DeploymentExecutor(
    AgentContext context,
    IServerLink serverLink,
    GrpcPackageDownloader packageDownloader,
    IEnumerable<IStepHandler> stepHandlers,
    IOptions<AgentConfig> agentConfig,
    ILogger<DeploymentExecutor> logger)
{
    private readonly IReadOnlyList<IStepHandler> _handlers = [.. stepHandlers];

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

    // ── Step execution ─────────────────────────────────────────────────────────

    private async Task<bool> ExecuteStepAsync(
        DeploymentPlan plan, DeploymentStepPlan step, CancellationToken ct)
    {
        await LogAsync(plan.DeploymentId, "info",
            $"--- Step {step.Index + 1}: {step.Name} ---", ct).ConfigureAwait(false);

        // Resolve a handler for this step type.
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(step.StepType));
        if (handler is null)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Unknown step type '{step.StepType}'. No handler is registered for it.", ct)
                .ConfigureAwait(false);
            return false;
        }

        var tempRoot = Path.Combine(
            agentConfig.Value.ResolvedDataPath, "staging",
            plan.DeploymentId.ToString("N"),
            step.Index.ToString(CultureInfo.InvariantCulture));

        Directory.CreateDirectory(tempRoot);

        var extractDir = string.Empty;

        // ── Package download + extract (skipped for steps that don't need it) ──
        if (handler.RequiresPackage && !string.IsNullOrWhiteSpace(step.PackageId))
        {
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

            extractDir = Path.Combine(tempRoot, "extracted");
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
        }
        else if (handler.RequiresPackage)
        {
            // Handler wants a package but none is configured — use the staging root.
            extractDir = tempRoot;
        }

        // ── Delegate to the handler ────────────────────────────────────────────
        bool success;
        try
        {
            var handlerCtx = new StepHandlerContext
            {
                Plan       = plan,
                Step       = step,
                ExtractDir = extractDir,
                LogAsync   = (level, msg) => LogAsync(plan.DeploymentId, level, msg, ct),
            };

            success = await handler.HandleAsync(handlerCtx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Step handler threw an unhandled exception: {ex.Message}", ct)
                .ConfigureAwait(false);
            success = false;
        }

        await LogAsync(plan.DeploymentId, success ? "info" : "error",
            success ? $"Step '{step.Name}' succeeded." : $"Step '{step.Name}' failed.",
            ct).ConfigureAwait(false);

        // ── Cleanup staging ────────────────────────────────────────────────────
        try { Directory.Delete(tempRoot, recursive: true); }
        catch { /* non-fatal */ }

        return success;
    }

    // ── Logging helper ─────────────────────────────────────────────────────────

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

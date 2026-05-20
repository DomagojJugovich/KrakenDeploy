using System.Globalization;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment.StepHandlers;
using KrakenDeploy.Agent.StepPackages;
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
    GrpcArtifactUploader artifactUploader,
    IEnumerable<IStepHandler> stepHandlers,
    StepPackageLoader stepPackageLoader,
    IOptions<AgentConfig> agentConfig,
    ILogger<DeploymentExecutor> logger)
{
    private readonly IReadOnlyList<IStepHandler> _handlers = [.. stepHandlers];

    /// <summary>
    /// True while a deployment is executing. Read by <see cref="Services.AgentUpdateService"/>
    /// to avoid swapping the agent binary during an in-flight deployment.
    /// </summary>
    public bool IsExecuting { get; private set; }

    public async Task ExecuteAsync(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        IsExecuting = true;
        try
        {

        logger.LogInformation(
            "Starting deployment {DeploymentId} ({StepCount} step(s)) in environment {Env}.",
            plan.DeploymentId, plan.Steps.Length, plan.EnvironmentName);

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Accumulates Set-OctopusVariable captures per step name across the run.
        // Made available to subsequent steps as Octopus.Action[StepName].Output.X.
        var outputsByStep = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var step in plan.Steps.OrderBy(s => s.Index))
            {
                // Build a per-step plan whose Variables include the output vars
                // captured from prior steps.
                var stepPlan = AugmentPlanWithPriorOutputs(plan, outputsByStep);

                var (success, capturedOutputs) =
                    await ExecuteStepAsync(stepPlan, step, ct).ConfigureAwait(false);

                if (capturedOutputs.Count > 0)
                {
                    outputsByStep[step.Name] = capturedOutputs;
                    try
                    {
                        await serverLink.ReportStepOutputVariablesAsync(
                            plan.DeploymentId, step.Name, capturedOutputs, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Failed to report output variables for step '{Step}' of deployment {Id}.",
                            step.Name, plan.DeploymentId);
                    }
                }

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
        finally
        {
            IsExecuting = false;
        }
    }

    // ── Step execution ─────────────────────────────────────────────────────────

    private async Task<(bool Success, Dictionary<string, string> CapturedOutputs)> ExecuteStepAsync(
        DeploymentPlan plan, DeploymentStepPlan step, CancellationToken ct)
    {
        await LogAsync(plan.DeploymentId, "info",
            $"--- Step {step.Index + 1}: {step.Name} ---", ct).ConfigureAwait(false);

        // Per-step bucket for Set-OctopusVariable captures. The wrapped LogAsync
        // intercepts ##octopus[...] markers and writes here instead of sending them
        // through as visible log lines.
        var capturedOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Resolve a handler. Two paths (Phase D-6):
        //   1. If the server pinned a StepPackageVersion, try the
        //      StepPackageLoader first — the package owns the step type and
        //      its handler takes precedence over any in-DI built-in that
        //      coincidentally claims the same step type. On cache miss the
        //      loader pulls the package via IStepPackageSource (D-5).
        //   2. Otherwise fall back to the in-DI handlers (the pre-D-6 path).
        //      Once D-8 has extracted every built-in into a package this
        //      fallback becomes the empty case and can be removed.
        var handler = await ResolveHandlerAsync(step, ct).ConfigureAwait(false);
        if (handler is null)
        {
            await LogAsync(plan.DeploymentId, "error",
                $"Unknown step type '{step.StepType}'. No handler is registered for it " +
                $"(pin={step.StepPackageName ?? "<null>"} {step.StepPackageVersion ?? "<null>"}).",
                ct).ConfigureAwait(false);
            return (false, capturedOutputs);
        }

        var tempRoot = Path.Combine(
            agentConfig.Value.ResolvedDataPath, "staging",
            plan.DeploymentId.ToString("N"),
            step.Index.ToString(CultureInfo.InvariantCulture));

        Directory.CreateDirectory(tempRoot);

        // Per-step artifacts directory — scripts write files here and they are
        // streamed back to the server after the step completes.
        var artifactsDir = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(artifactsDir);

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
                return (false, capturedOutputs);
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
                return (false, capturedOutputs);
            }
        }
        else if (handler.RequiresPackage)
        {
            // Handler wants a package but none is configured — use the staging root.
            extractDir = tempRoot;
        }

        // ── Referenced package download + extract ─────────────────────────────
        // For steps that declare Octopus.Action.Package.PackageReferences,
        // extract each one to extract/refs/<Name>/ and expose its path as an
        // env var / system variable (handled by the step handler).
        var refExtractRoot = string.Empty;
        var referencedExtractedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (step.ReferencedPackages is { Count: > 0 } refs)
        {
            refExtractRoot = Path.Combine(string.IsNullOrEmpty(extractDir) ? tempRoot : extractDir, "refs");
            Directory.CreateDirectory(refExtractRoot);

            var identity = context.Identity!;
            foreach (var r in refs)
            {
                if (string.IsNullOrWhiteSpace(r.Version))
                {
                    await LogAsync(plan.DeploymentId, "warning",
                        $"Referenced package '{r.Name}' ({r.PackageId}) has no resolved version; skipping.", ct)
                        .ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await LogAsync(plan.DeploymentId, "info",
                        $"Downloading referenced package '{r.Name}': {r.PackageId} v{r.Version}…", ct)
                        .ConfigureAwait(false);

                    var refZipPath = await packageDownloader
                        .DownloadAsync(identity.ServerUrl, identity.AgentToken,
                            r.PackageId, r.Version, refExtractRoot, ct)
                        .ConfigureAwait(false);

                    if (r.Extract)
                    {
                        var refDir = Path.Combine(refExtractRoot, SanitisePathSegment(r.Name));
                        await PackageExtractor.ExtractAsync(refZipPath, refDir, ct).ConfigureAwait(false);
                        referencedExtractedPaths[r.Name] = refDir;
                    }
                    else
                    {
                        referencedExtractedPaths[r.Name] = refZipPath;
                    }
                }
                catch (Exception ex)
                {
                    await LogAsync(plan.DeploymentId, "error",
                        $"Failed to fetch referenced package '{r.Name}': {ex.Message}", ct)
                        .ConfigureAwait(false);
                    return (false, capturedOutputs);
                }
            }
        }

        // ── Delegate to the handler ────────────────────────────────────────────
        // ##octopus[...] marker interceptor: a "sticky" log level set by
        // ##octopus[stdout-warning|error|default] persists until overridden.
        var stickyLevel = "info";

        async Task InterceptingLogAsync(string level, string message)
        {
            var msg = OctopusMessageParser.TryParse(message);
            switch (msg)
            {
                case SetVariableMessage v:
                    capturedOutputs[v.Name] = v.Value;
                    return; // marker is not user-visible log output
                case SetLogLevelMessage l:
                    stickyLevel = l.Level;
                    return;
                case CreateArtifactMessage a:
                    // Artifact files are collected from the artifacts dir after the
                    // step; the marker itself is informational.
                    await LogAsync(plan.DeploymentId, "info",
                        $"[Artifact] {a.Name} ({a.Path})", ct).ConfigureAwait(false);
                    return;
                case ProgressMessage p:
                    await LogAsync(plan.DeploymentId, "info",
                        $"[Progress {p.Percentage}%] {p.Message}", ct).ConfigureAwait(false);
                    return;
                case UnknownMessage u:
                    logger.LogDebug(
                        "Unknown ##octopus[{Cmd}] directive in step '{Step}'; passing through as a log line.",
                        u.Command, step.Name);
                    break;
            }

            // Plain log line — apply sticky level if it overrides "info".
            var effectiveLevel = level.Equals("info", StringComparison.OrdinalIgnoreCase)
                ? stickyLevel
                : level;
            await LogAsync(plan.DeploymentId, effectiveLevel, message, ct).ConfigureAwait(false);
        }

        bool success;
        try
        {
            var handlerCtx = new StepHandlerContext
            {
                Plan                   = plan,
                Step                   = step,
                ExtractDir             = extractDir,
                ArtifactsDir           = artifactsDir,
                LogAsync               = InterceptingLogAsync,
                ReferencedPackagePaths = referencedExtractedPaths,
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

        // ── Artifact collection ────────────────────────────────────────────────
        await CollectArtifactsAsync(plan, step, artifactsDir, ct).ConfigureAwait(false);

        // ── Cleanup staging ────────────────────────────────────────────────────
        try { Directory.Delete(tempRoot, recursive: true); }
        catch { /* non-fatal */ }

        return (success, capturedOutputs);
    }

    /// <summary>
    /// Phase D-6 handler resolution: when the plan pins a step-package
    /// version, ask the loader (downloading on cache miss). Falls back to
    /// the in-DI handler list when no pin is set OR when the loader can't
    /// produce a handler. Returns <c>null</c> when nothing claims the step
    /// type — the caller surfaces an error to the deployment log.
    /// </summary>
    private async Task<IStepHandler?> ResolveHandlerAsync(
        DeploymentStepPlan step, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(step.StepPackageName)
            && !string.IsNullOrWhiteSpace(step.StepPackageVersion))
        {
            try
            {
                var pkg = await stepPackageLoader
                    .TryLoadOrDownloadAsync(step.StepPackageName, step.StepPackageVersion, ct)
                    .ConfigureAwait(false);

                if (pkg is not null)
                {
                    // Activator-created — per-step-execution lifecycle.
                    if (Activator.CreateInstance(pkg.HandlerType) is IStepHandler instance
                        && instance.CanHandle(step.StepType))
                    {
                        return instance;
                    }

                    logger.LogWarning(
                        "Step package {Name} {Version} loaded but its handler doesn't accept step type '{StepType}'. " +
                        "Falling back to in-DI handlers.",
                        step.StepPackageName, step.StepPackageVersion, step.StepType);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Step package {Name} {Version} failed to load; falling back to in-DI handlers.",
                    step.StepPackageName, step.StepPackageVersion);
            }
        }

        return _handlers.FirstOrDefault(h => h.CanHandle(step.StepType));
    }

    /// <summary>
    /// Replaces filesystem-unfriendly characters in a reference name so it can
    /// be used as a directory segment. The original name is still surfaced as
    /// <c>Octopus.Action.Package[Name].ExtractedPath</c>; this is only the
    /// on-disk path. Mirrors Octopus's behaviour: dots, dashes, alphanumerics
    /// kept; everything else collapsed to underscore.
    /// </summary>
    private static string SanitisePathSegment(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }
        var safe = sb.ToString();
        return string.IsNullOrEmpty(safe) ? "pkg" : safe;
    }

    // ── Output-variable plumbing ───────────────────────────────────────────────

    /// <summary>
    /// Returns a copy of the plan with <c>Octopus.Action[StepName].Output.X</c>
    /// keys merged into <see cref="DeploymentPlan.Variables"/> for every
    /// previously-completed step's captured output variables.
    /// </summary>
    private static DeploymentPlan AugmentPlanWithPriorOutputs(
        DeploymentPlan basePlan,
        Dictionary<string, Dictionary<string, string>> outputsByStep)
    {
        if (outputsByStep.Count == 0)
        {
            return basePlan;
        }

        var merged = new Dictionary<string, string>(basePlan.Variables, StringComparer.OrdinalIgnoreCase);
        foreach (var (stepName, outputs) in outputsByStep)
        {
            foreach (var (name, value) in outputs)
            {
                merged[$"Octopus.Action[{stepName}].Output.{name}"] = value;
            }
        }

        return basePlan with { Variables = merged };
    }

    // ── Artifact collection ────────────────────────────────────────────────────

    private async Task CollectArtifactsAsync(
        DeploymentPlan plan,
        DeploymentStepPlan step,
        string artifactsDir,
        CancellationToken ct)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(artifactsDir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return; // directory was cleaned up or never created — nothing to do
        }

        if (files.Length == 0)
        {
            return;
        }

        var identity = context.Identity;
        if (identity is null)
        {
            logger.LogWarning(
                "Cannot upload artifacts for step '{StepName}' — agent identity not available.",
                step.Name);
            return;
        }

        await LogAsync(plan.DeploymentId, "info",
            $"Collecting {files.Length} artifact(s) from step '{step.Name}'…", ct)
            .ConfigureAwait(false);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var artifactId = await artifactUploader.UploadAsync(
                identity.ServerUrl, identity.AgentToken,
                plan.DeploymentId, step.Name, filePath, ct)
                .ConfigureAwait(false);

            if (artifactId is not null)
            {
                var rel = Path.GetRelativePath(artifactsDir, filePath);
                await LogAsync(plan.DeploymentId, "info",
                    $"Artifact collected: {rel}", ct).ConfigureAwait(false);
            }
        }
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

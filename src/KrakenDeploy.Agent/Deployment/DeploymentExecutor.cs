using System.Globalization;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Steps;
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
    StepPackageLoader stepPackageLoader,
    IOptions<AgentConfig> agentConfig,
    ILogger<DeploymentExecutor> logger)
{

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
        // M14.4: inside a parallel wave siblings DO NOT see each other's
        // outputs — we snapshot pre-wave state once, run the wave's steps
        // against that frozen snapshot, and merge captures into the
        // accumulator only AFTER the wave completes (in SortOrder, so
        // last-writer-wins by Index when names collide).
        var outputsByStep = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            // ── M14.4 — partition into waves agent-side ─────────────────
            // The server already pre-flattens waves (one wave per sub-plan
            // dispatched), but the agent re-walks the trigger field so its
            // behaviour stays correct if the contract evolves to send
            // multi-wave sub-plans in the future.
            var waves = PartitionIntoWaves(plan.Steps);

            string? firstFailureMessage = null;
            var anyStepFailed = false;

            foreach (var wave in waves)
            {
                // Snapshot prior outputs once before the wave so all siblings
                // see the same baseline — siblings don't see each other's
                // captures.
                var preWavePlan = AugmentPlanWithPriorOutputs(plan, outputsByStep);

                var stepTasks = wave.Select(step =>
                    RunStepInWaveAsync(plan, preWavePlan, step, ct)).ToArray();

                var stepOutcomes = await Task.WhenAll(stepTasks).ConfigureAwait(false);

                // Merge captures into accumulator in declared order
                // (wave order == SortOrder ascending, set in PartitionIntoWaves)
                // so last-writer-wins by Index for any name overlaps.
                foreach (var outcome in stepOutcomes)
                {
                    if (outcome.CapturedOutputs.Count > 0)
                    {
                        // M15.2: key by AccumulatorKey (ForEach-iteration
                        // synthetic name like "Deploy[0]") when set. Falls
                        // back to step.Name for non-iteration steps + pre-M15
                        // plans. Octostache references resolve to the same
                        // key on the server side.
                        var accKey = outcome.Step.AccumulatorKey ?? outcome.Step.Name;
                        outputsByStep[accKey] = outcome.CapturedOutputs;
                    }
                    if (!outcome.Success && firstFailureMessage is null)
                    {
                        firstFailureMessage = $"Step '{outcome.Step.Name}' failed.";
                    }
                    if (!outcome.Success)
                    {
                        anyStepFailed = true;
                    }
                }

                // If any step in the wave failed, stop dispatching further
                // waves agent-side. Per-step Required attribution happens
                // server-side (the orchestrator drained per-step reports
                // from the registry).
                if (anyStepFailed)
                {
                    break;
                }
            }

            await serverLink
                .CompleteDeploymentAsync(
                    plan.DeploymentId,
                    success:      !anyStepFailed,
                    errorMessage: firstFailureMessage,
                    ct)
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

    /// <summary>
    /// One step's outcome inside a wave. <see cref="Step"/> is kept on the
    /// record so the wave-merger can index outputs by step name without
    /// holding a parallel array.
    /// </summary>
    private sealed record StepOutcome(
        DeploymentStepPlan Step,
        bool Success,
        Dictionary<string, string> CapturedOutputs);

    /// <summary>
    /// M14.4 — agent-side wave partitioner. Same algorithm as the server's
    /// <see cref="Server.Transport.WavePartitioner"/> (kept duplicated so
    /// the agent stays loosely coupled to the server-side helper; the
    /// contract field <see cref="DeploymentStepPlan.StartTrigger"/> is
    /// the source of truth). A wave = first step + all subsequent
    /// <see cref="StepStartTrigger.StartWithPrevious"/> steps until the
    /// next <see cref="StepStartTrigger.StartAfterPrevious"/> opens a new
    /// wave. First step's trigger is ignored.
    /// </summary>
    private static List<List<DeploymentStepPlan>> PartitionIntoWaves(
        DeploymentStepPlan[] steps)
    {
        var waves = new List<List<DeploymentStepPlan>>();
        if (steps.Length == 0)
        {
            return waves;
        }

        var ordered = steps.OrderBy(s => s.Index).ToArray();
        var current = new List<DeploymentStepPlan> { ordered[0] };

        // StepStartTrigger int values: 0 = StartAfterPrevious, 1 = StartWithPrevious.
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].StartTrigger == 1)
            {
                current.Add(ordered[i]);
            }
            else
            {
                waves.Add(current);
                current = [ordered[i]];
            }
        }
        waves.Add(current);
        return waves;
    }

    /// <summary>
    /// Runs a single step inside a wave, capturing outputs locally then
    /// reporting the per-step boundary to the server via M14.4's
    /// <see cref="IServerLink.ReportStepCompletedAsync"/>. Catches handler
    /// exceptions so one step's failure doesn't tear down sibling
    /// <c>Task.WhenAll</c> branches.
    /// </summary>
    private async Task<StepOutcome> RunStepInWaveAsync(
        DeploymentPlan basePlan,
        DeploymentPlan preWavePlan,
        DeploymentStepPlan step,
        CancellationToken ct)
    {
        bool success;
        Dictionary<string, string> capturedOutputs;
        string? errorMessage = null;
        try
        {
            (success, capturedOutputs) =
                await ExecuteStepAsync(preWavePlan, step, ct).ConfigureAwait(false);
            if (!success)
            {
                errorMessage = $"Step '{step.Name}' failed.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Re-throw so Task.WhenAll surfaces cancellation cleanly to the
            // outer dispatcher; nothing to report per-step (the server's
            // CT cancel is the source of truth).
            throw;
        }
        catch (Exception ex)
        {
            success = false;
            capturedOutputs = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            errorMessage = ex.Message;
            logger.LogError(ex,
                "Step '{Step}' threw during wave execution (deployment {Id}).",
                step.Name, basePlan.DeploymentId);
        }

        // M15.2 — report by AccumulatorKey when set (ForEach iterations).
        // Falls back to step.Name for non-iteration steps + pre-M15 plans
        // where AccumulatorKey is null. The accumulator key is what the
        // server stores against the output variables so cross-iteration
        // Octostache references via #{Octopus.Action[Deploy[0]].Output.X}
        // resolve correctly.
        var reportingKey = step.AccumulatorKey ?? step.Name;
        try
        {
            await serverLink.ReportStepCompletedAsync(
                basePlan.DeploymentId,
                stepIndex:       step.Index,
                stepName:        reportingKey,
                success:         success,
                errorMessage:    errorMessage,
                outputVariables: capturedOutputs,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to report step completion for '{Step}' of deployment {Id}.",
                reportingKey, basePlan.DeploymentId);
        }

        return new StepOutcome(step, success, capturedOutputs);
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
    /// Phase D-6 handler resolution (D-8.9: package-only, no in-DI fallback).
    /// Every step must have a <see cref="DeploymentStepPlan.StepPackageName"/>
    /// + <see cref="DeploymentStepPlan.StepPackageVersion"/> pin — set by
    /// <c>ProcessService.AddStepAsync</c> on author and re-resolved on
    /// release snapshot. The loader downloads the package on cache miss
    /// (gRPC) and instantiates the handler via Activator. Returns
    /// <c>null</c> when:
    /// <list type="bullet">
    ///   <item>the plan has no pin (the server should have failed earlier),</item>
    ///   <item>the loader can't find or load the package,</item>
    ///   <item>the loaded handler refuses the step type.</item>
    /// </list>
    /// The caller surfaces all three as an "unknown step type" error.
    /// Per the pre-prod policy in docs/architecture.md, there is no
    /// fallback to a hardcoded in-DI handler.
    /// </summary>
    private async Task<IStepHandler?> ResolveHandlerAsync(
        DeploymentStepPlan step, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.StepPackageName)
            || string.IsNullOrWhiteSpace(step.StepPackageVersion))
        {
            logger.LogError(
                "Step '{StepName}' (type {StepType}) has no step-package pin — " +
                "server failed to resolve a package for this step type at " +
                "release-creation. Re-create the release after installing a " +
                "package that claims this step type.",
                step.Name, step.StepType);
            return null;
        }

        try
        {
            var pkg = await stepPackageLoader
                .TryLoadOrDownloadAsync(step.StepPackageName, step.StepPackageVersion, ct)
                .ConfigureAwait(false);

            if (pkg is null) { return null; }

            // Activator-created — per-step-execution lifecycle.
            if (Activator.CreateInstance(pkg.HandlerType) is IStepHandler instance
                && instance.CanHandle(step.StepType))
            {
                return instance;
            }

            logger.LogError(
                "Step package {Name} {Version} loaded but its handler doesn't accept step type '{StepType}'.",
                step.StepPackageName, step.StepPackageVersion, step.StepType);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Step package {Name} {Version} failed to load.",
                step.StepPackageName, step.StepPackageVersion);
            return null;
        }
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
    /// M15.2 + follow-up — delegates to
    /// <see cref="OutputVariableAccumulator.AugmentPlanWithPriorOutputs"/>.
    /// The accumulator was extracted from this file so the cross-iteration
    /// reference contract can be unit-tested without spinning up the full
    /// executor; this wrapper preserves the original call site for the
    /// per-wave snapshot path.
    /// </summary>
    private static DeploymentPlan AugmentPlanWithPriorOutputs(
        DeploymentPlan basePlan,
        Dictionary<string, Dictionary<string, string>> outputsByStep)
        => OutputVariableAccumulator.AugmentPlanWithPriorOutputs(basePlan, outputsByStep);

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

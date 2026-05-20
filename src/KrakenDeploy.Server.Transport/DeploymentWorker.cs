using System.Text.Json;
using System.Threading.Channels;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Background service that reads deployment IDs from the in-process channel,
/// resolves the target agent's SignalR connection, and sends the
/// <see cref="DeploymentPlan"/> to the agent.
/// <para>
/// Before building the plan the worker:
/// <list type="number">
///   <item>Resolves project variables for the specific environment / target / roles.</item>
///   <item>Applies Octostache <c>#{VarName}</c> substitution to step Config values.</item>
///   <item>Splits <see cref="VariableService"/> StringArray values into the
///         <see cref="DeploymentPlan.ArrayVariables"/> dictionary for the agent's
///         <c>$OctopusArrays</c> PowerShell exposure.</item>
/// </list>
/// </para>
/// <para>
/// The agent executes the plan autonomously and reports back via
/// <see cref="AgentHub.AppendLogAsync"/> and
/// <see cref="AgentHub.CompleteDeploymentAsync"/>.
/// </para>
/// </summary>
public sealed class DeploymentWorker(
    Channel<Guid> queue,
    IAgentConnectionRegistry registry,
    IHubContext<AgentHub, IAgentHubClient> agentHub,
    ServerScriptStepRunner serverRunner,
    DeployReleaseStepRunner deployReleaseRunner,
    IPendingSubPlanRegistry subPlans,
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var deploymentId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Process fire-and-forget; don't block the reader loop.
            _ = DispatchAsync(deploymentId, stoppingToken);
        }
    }

    private async Task DispatchAsync(Guid deploymentId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var variableService = scope.ServiceProvider.GetRequiredService<VariableService>();
        var serverBaseUrl = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["Server:BaseUrl"];

        try
        {
            var deployment = await db.Deployments
                .Include(d => d.Release)
                    .ThenInclude(r => r.Project)
                .Include(d => d.Environment)
                .Include(d => d.Target)
                .Include(d => d.Tenant)
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
                .ConfigureAwait(false);

            if (deployment is null)
            {
                logger.LogWarning("DeploymentWorker: deployment {Id} not found.", deploymentId);
                return;
            }

            if (deployment.TargetId is null)
            {
                await FailAsync(db, deployment, "No target assigned to deployment.", ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── Offline drop path ───────────────────────────────────────────
            if (deployment.Target?.TransportMode == TransportMode.OfflineDrop)
            {
                await DispatchOfflineDropAsync(scope.ServiceProvider, db, deployment, ct)
                    .ConfigureAwait(false);
                return;
            }

            // ── 1. Resolve project variables ─────────────────────────────────
            // (Agent-connection check is deferred until after we know whether
            // any target-side steps need dispatching — fully-server-side
            // deployments don't require an online agent.)
            var targetRoles = deployment.Target?.Roles ?? [];
            var rawVars = await variableService.ResolveAsync(
                deployment.Release.ProjectId,
                deployment.EnvironmentId,
                deployment.TargetId,
                targetRoles,
                deployment.TenantId,
                ct).ConfigureAwait(false);

            // ── 2. Build Octostache dictionary ───────────────────────────────
            // Scalar (String + Sensitive) variables go straight into Octostache.
            // StringArray variables are expanded as both VarName (comma-joined)
            // and VarName[0], VarName[1], … for indexed / #{each} access.
            var varDict = new VariableDictionary();

            // Octopus-compatible system variables (Octopus.Project.Name, Octopus.Release.Number, …).
            var systemVars = OctopusSystemVariablesBuilder.BuildForDeployment(
                deployment,
                deployment.Release,
                deployment.Release.Project,
                deployment.Environment,
                deployment.Target,
                deployment.Tenant,
                deployment.Release.ProcessSnapshot,
                serverBaseUrl);

            var flatVars = new Dictionary<string, string>(systemVars, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, val) in systemVars)
            {
                varDict[k] = val;
            }

            var arrayVars = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in rawVars)
            {
                if (value.StartsWith('['))
                {
                    // StringArray: try to parse as JSON array.
                    try
                    {
                        var items = JsonSerializer.Deserialize<string[]>(value) ?? [];
                        arrayVars[name] = items;

                        // Comma-joined for $OctopusParameters back-compat and Octostache #{VarName}.
                        var joined = string.Join(", ", items);
                        flatVars[name] = joined;
                        varDict[name] = joined;

                        // Indexed access for #{VarName[0]}, #{each x in VarName}.
                        for (var i = 0; i < items.Length; i++)
                        {
                            varDict[$"{name}[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"] = items[i];
                        }

                        continue;
                    }
                    catch (JsonException)
                    {
                        // Not valid JSON — treat as plain string.
                    }
                }

                flatVars[name] = value;
                varDict[name] = value;
            }

            // ── 3. Build steps with Octostache substitution applied + resolve
            //       referenced packages (Octopus.Action.Package.PackageReferences).
            var dbFactory = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>();

            var snapshotSteps = deployment.Release.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .ToArray();

            var steps = new DeploymentStepPlan[snapshotSteps.Length];
            for (var i = 0; i < snapshotSteps.Length; i++)
            {
                var s = snapshotSteps[i];
                var substitutedConfig = SubstituteConfig(s.Config, varDict);
                var referenced = await PackageReferenceResolver
                    .ResolveAsync(substitutedConfig, dbFactory, logger, ct)
                    .ConfigureAwait(false);
                steps[i] = new DeploymentStepPlan(
                    Index: i,
                    Name: s.Name,
                    StepType: s.StepType,
                    PackageId: s.PackageId,
                    PackageVersion: s.PackageVersion,
                    Config: substitutedConfig,
                    TargetRoles: s.TargetRoles,
                    ReferencedPackages: referenced.Count > 0 ? referenced : null,
                    StepPackageName: s.StepPackageName,
                    StepPackageVersion: s.StepPackageVersion);
            }

            var plan = new DeploymentPlan(
                DeploymentId: deployment.Id,
                EnvironmentName: deployment.Environment.Name,
                Steps: steps,
                Variables: flatVars,
                ArrayVariables: arrayVars);

            // ── 4. Partition steps into consecutive same-side groups ─────────
            // Each group is either entirely server-side or entirely target-
            // side; we run them in declared order, dispatching target groups
            // to the agent piecewise and awaiting completion before continuing.
            // This supports any ordering — e.g. target → server → target — by
            // making multiple round trips with the agent.
            var groups = PartitionIntoGroups(steps);

            // Transition to Running before doing any work so the UI updates immediately.
            deployment.Status     = DeploymentStatus.Running;
            deployment.StartedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var serverStepCount = groups.Where(g => g.IsServer).Sum(g => g.Steps.Length);
            var targetStepCount = groups.Where(g => !g.IsServer).Sum(g => g.Steps.Length);
            logger.LogInformation(
                "Deployment {DeploymentId}: {Groups} group(s), {ServerSteps} server step(s), " +
                "{TargetSteps} target step(s), {VarCount} variables.",
                deploymentId, groups.Count, serverStepCount, targetStepCount, flatVars.Count);

            foreach (var group in groups)
            {
                if (group.IsServer)
                {
                    // Run each server step in-process. Honours
                    // "Server on behalf of each deployment target" via the
                    // role-filter helper. Dispatch by StepType so server-only
                    // orchestrator steps (e.g. Octopus.DeployRelease) route to
                    // a dedicated runner instead of the generic script runner.
                    foreach (var s in group.Steps)
                    {
                        if (!StepAppliesToTarget(deployment, s))
                        {
                            continue;
                        }

                        var ok = await ExecuteServerStepAsync(deployment.Id, s, flatVars, ct)
                            .ConfigureAwait(false);
                        if (!ok)
                        {
                            await FailAsync(db, deployment,
                                $"Server-side step '{s.Name}' failed.", ct).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                else
                {
                    // Target group: send a sub-plan to the agent and await its
                    // completion before moving on.
                    var connectionId = registry.GetConnectionId(deployment.TargetId.Value);
                    if (connectionId is null)
                    {
                        await FailAsync(db, deployment, "Target is offline.", ct).ConfigureAwait(false);
                        return;
                    }

                    var subPlan = plan with { Steps = group.Steps };

                    var tcs = new TaskCompletionSource<SubPlanResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    subPlans.Register(deployment.Id, tcs);

                    SubPlanResult subPlanResult;
                    try
                    {
                        await agentHub.Clients.Client(connectionId)
                            .RunDeploymentAsync(subPlan).ConfigureAwait(false);

                        using var ctr = ct.Register(
                            () => tcs.TrySetCanceled(ct));
                        subPlanResult = await tcs.Task.ConfigureAwait(false);
                    }
                    finally
                    {
                        subPlans.Cancel(deployment.Id, "completed");
                    }

                    if (!subPlanResult.Success)
                    {
                        await FailAsync(db, deployment,
                            subPlanResult.ErrorMessage ?? "Agent reported failure", ct)
                            .ConfigureAwait(false);
                        return;
                    }
                }
            }

            // All groups succeeded — finalize.
            await using (var finalDb = await scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                .CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var d = await finalDb.Deployments.FindAsync([deployment.Id], ct).ConfigureAwait(false);
                if (d is not null)
                {
                    d.Status       = DeploymentStatus.Succeeded;
                    d.CompletedUtc = DateTimeOffset.UtcNow;
                    await finalDb.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }

            logger.LogInformation(
                "Deployment {Id} completed ({ServerSteps} server step(s), {TargetSteps} target step(s)).",
                deployment.Id, serverStepCount, targetStepCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Unhandled error dispatching deployment {DeploymentId}.", deploymentId);

            await using var errorScope = scopeFactory.CreateAsyncScope();
            var errorDb = errorScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            var dep = await errorDb.Deployments.FindAsync([deploymentId], ct).ConfigureAwait(false);
            if (dep is not null)
            {
                await FailAsync(errorDb, dep, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Offline drop ─────────────────────────────────────────────────────

    private async Task DispatchOfflineDropAsync(
        IServiceProvider sp, KrakenDbContext db, Deployment deployment, CancellationToken ct)
    {
        var variableService = sp.GetRequiredService<VariableService>();
        var dropBundleService = sp.GetRequiredService<DropBundleService>();
        var config = sp.GetRequiredService<IConfiguration>();
        var dataPath = config["DataPath"] ?? "data";

        var targetRoles = deployment.Target?.Roles ?? [];
        var rawVars = await variableService.ResolveAsync(
            deployment.Release.ProjectId,
            deployment.EnvironmentId,
            deployment.TargetId,
            targetRoles,
            deployment.TenantId,
            ct).ConfigureAwait(false);

        // Flatten variables to string dictionary (same as online path).
        var systemVars = OctopusSystemVariablesBuilder.BuildForDeployment(
            deployment,
            deployment.Release,
            deployment.Release.Project,
            deployment.Environment,
            deployment.Target,
            deployment.Tenant,
            deployment.Release.ProcessSnapshot,
            config["Server:BaseUrl"]);

        var flatVars = new Dictionary<string, string>(systemVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in rawVars)
        {
            flatVars[name] = value;
        }

        var bundlePath = await dropBundleService
            .GenerateAsync(deployment, flatVars, dataPath, ct)
            .ConfigureAwait(false);

        deployment.DropBundlePath = bundlePath;
        deployment.Status = DeploymentStatus.PendingOfflineResult;
        deployment.StartedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Offline drop bundle generated for deployment {DeploymentId}: {Path}.",
            deployment.Id, bundlePath);

        // Attempt delivery if configured (non-Manual).
        await DeliverDropBundleAsync(sp, deployment, dataPath, ct).ConfigureAwait(false);
    }

    private async Task DeliverDropBundleAsync(
        IServiceProvider sp, Deployment deployment, string dataPath, CancellationToken ct)
    {
        var deliveryChannel = deployment.Target?.OfflineDropConfig?.DeliveryChannel
            ?? OfflineDropDeliveryChannel.Manual;

        if (deliveryChannel == OfflineDropDeliveryChannel.Manual ||
            string.IsNullOrEmpty(deployment.DropBundlePath))
        {
            return; // User will download manually from the UI
        }

        try
        {
            switch (deliveryChannel)
            {
                case OfflineDropDeliveryChannel.Webhook:
                    await DeliverViaWebhookAsync(deployment, dataPath, ct).ConfigureAwait(false);
                    break;
                case OfflineDropDeliveryChannel.FileShareDrop:
                    await DeliverViaFileShareAsync(deployment, dataPath, ct).ConfigureAwait(false);
                    break;
                case OfflineDropDeliveryChannel.Email:
                    logger.LogWarning(
                        "Email delivery not yet implemented for deployment {Id}. " +
                        "Bundle is available for manual download.",
                        deployment.Id);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Delivery failure is non-fatal — the bundle is still available for download.
            logger.LogError(ex,
                "Failed to deliver drop bundle for deployment {Id} via {Channel}. " +
                "Bundle is available for manual download.",
                deployment.Id, deliveryChannel);
        }
    }

    private async Task DeliverViaWebhookAsync(
        Deployment deployment, string dataPath, CancellationToken ct)
    {
        var webhookUrl = deployment.Target?.OfflineDropConfig?.WebhookUrl;
        if (string.IsNullOrEmpty(webhookUrl))
        {
            return;
        }

        var bundleFullPath = Path.Combine(
            dataPath,
            deployment.DropBundlePath!.Replace('/', Path.DirectorySeparatorChar));

        using var httpClient = new HttpClient();
        await using var stream = new FileStream(
            bundleFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        content.Headers.Add("X-Kraken-Deployment-Id", deployment.Id.ToString());

        var response = await httpClient.PostAsync(webhookUrl, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        logger.LogInformation(
            "Drop bundle for deployment {Id} delivered via webhook to {Url}.",
            deployment.Id, webhookUrl);
    }

    private async Task DeliverViaFileShareAsync(
        Deployment deployment, string dataPath, CancellationToken ct)
    {
        var targetPath = deployment.Target?.OfflineDropConfig?.FileSharePath;
        if (string.IsNullOrEmpty(targetPath))
        {
            return;
        }

        var bundleFullPath = Path.Combine(
            dataPath,
            deployment.DropBundlePath!.Replace('/', Path.DirectorySeparatorChar));

        var destDir = Path.Combine(targetPath, deployment.Id.ToString());
        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, Path.GetFileName(bundleFullPath));

        await using var source = new FileStream(
            bundleFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dest = new FileStream(
            destFile, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(dest, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Drop bundle for deployment {Id} copied to file share: {Path}.",
            deployment.Id, destFile);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// A consecutive run of same-side steps (all server-side or all target-side).
    /// </summary>
    private sealed record StepGroup(bool IsServer, DeploymentStepPlan[] Steps);

    /// <summary>
    /// Partition steps into consecutive same-side groups in declared order.
    /// E.g. [target, target, server, target] → [Target(2), Server(1), Target(1)].
    /// </summary>
    private static List<StepGroup> PartitionIntoGroups(DeploymentStepPlan[] steps)
    {
        var groups = new List<StepGroup>();
        if (steps.Length == 0)
        {
            return groups;
        }

        var ordered = steps.OrderBy(s => s.Index).ToArray();
        var current = new List<DeploymentStepPlan> { ordered[0] };
        var currentIsServer = IsServerStep(ordered[0]);

        for (var i = 1; i < ordered.Length; i++)
        {
            var s      = ordered[i];
            var isSrv  = IsServerStep(s);
            if (isSrv == currentIsServer)
            {
                current.Add(s);
            }
            else
            {
                groups.Add(new StepGroup(currentIsServer, [.. current]));
                current = [s];
                currentIsServer = isSrv;
            }
        }
        groups.Add(new StepGroup(currentIsServer, [.. current]));
        return groups;
    }

    /// <summary>
    /// True if the step's config marks it for server-side execution. A step is
    /// server-side when EITHER the config carries
    /// <c>Octopus.Action.RunOnServer = "true"</c> (the explicit Octopus marker),
    /// OR the <see cref="DeploymentStepPlan.StepType"/> is one of the
    /// intrinsically server-side orchestrator types (<see cref="ServerOnlyStepTypes"/>).
    /// </summary>
    private static bool IsServerStep(DeploymentStepPlan step)
    {
        if (step.Config.TryGetValue("Octopus.Action.RunOnServer", out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return ServerOnlyStepTypes.Contains(step.StepType);
    }

    /// <summary>
    /// Step types that always run on the server regardless of the
    /// <c>Octopus.Action.RunOnServer</c> flag — they coordinate other deployments
    /// or otherwise have no agent-side meaning.
    /// </summary>
    private static readonly HashSet<string> ServerOnlyStepTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            DeployReleaseStepRunner.StepType,
        };

    /// <summary>
    /// Dispatches one server-side step to the appropriate runner based on its
    /// <see cref="DeploymentStepPlan.StepType"/>. Orchestrator step types (like
    /// <c>Octopus.DeployRelease</c>) route to a dedicated runner; everything
    /// else falls through to the generic <see cref="ServerScriptStepRunner"/>.
    /// </summary>
    private Task<bool> ExecuteServerStepAsync(
        Guid deploymentId,
        DeploymentStepPlan step,
        IReadOnlyDictionary<string, string> flatVars,
        CancellationToken ct)
    {
        if (step.StepType.Equals(DeployReleaseStepRunner.StepType, StringComparison.OrdinalIgnoreCase))
        {
            return deployReleaseRunner.ExecuteAsync(deploymentId, step, flatVars, ct);
        }
        return serverRunner.ExecuteAsync(deploymentId, step, flatVars, ct);
    }

    /// <summary>
    /// Encapsulates "Run on Server on behalf of each deployment target" role
    /// filtering for our one-target-per-deployment model: when a server step
    /// has <c>TargetRoles</c>, only execute it if the deployment's target has
    /// at least one of those roles. A server step without roles always
    /// applies (it's a pure "Run on Server" step).
    /// </summary>
    private static bool StepAppliesToTarget(Deployment deployment, DeploymentStepPlan step)
    {
        if (step.TargetRoles is null || step.TargetRoles.Count == 0)
        {
            return true;
        }
        var targetRoles = deployment.Target?.Roles ?? [];
        if (targetRoles.Count == 0)
        {
            return false;
        }
        return step.TargetRoles.Any(r =>
            targetRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies Octostache variable substitution to all values in a step's Config dictionary.
    /// Keys are never substituted (they are well-known step-type contract strings).
    /// </summary>
    private static Dictionary<string, string> SubstituteConfig(
        Dictionary<string, string> config,
        VariableDictionary vars)
    {
        if (config.Count == 0)
        {
            return config;
        }

        return config.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var evaluated = vars.Evaluate(kv.Value);
                return evaluated ?? kv.Value;
            });
    }

    private static async Task FailAsync(
        KrakenDbContext db, Deployment deployment, string reason, CancellationToken ct)
    {
        deployment.Status = DeploymentStatus.Failed;
        deployment.CompletedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

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

            var connectionId = registry.GetConnectionId(deployment.TargetId.Value);
            if (connectionId is null)
            {
                await FailAsync(db, deployment, "Target is offline.", ct).ConfigureAwait(false);
                return;
            }

            // ── 1. Resolve project variables ─────────────────────────────────
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

            // ── 3. Build steps with Octostache substitution applied ──────────
            var steps = deployment.Release.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .Select((s, i) => new DeploymentStepPlan(
                    Index: i,
                    Name: s.Name,
                    StepType: s.StepType,
                    PackageId: s.PackageId,
                    PackageVersion: s.PackageVersion,
                    Config: SubstituteConfig(s.Config, varDict)))
                .ToArray();

            var plan = new DeploymentPlan(
                DeploymentId: deployment.Id,
                EnvironmentName: deployment.Environment.Name,
                Steps: steps,
                Variables: flatVars,
                ArrayVariables: arrayVars);

            // Transition to Running before sending so the UI updates immediately.
            deployment.Status = DeploymentStatus.Running;
            deployment.StartedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Dispatching deployment {DeploymentId} to connection {ConnectionId} " +
                "({VarCount} variables, {ArrayCount} array variables).",
                deploymentId, connectionId, flatVars.Count, arrayVars.Count);

            await agentHub.Clients.Client(connectionId)
                .RunDeploymentAsync(plan)
                .ConfigureAwait(false);
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

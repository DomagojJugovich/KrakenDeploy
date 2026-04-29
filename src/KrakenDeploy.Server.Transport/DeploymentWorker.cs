using System.Text.Json;
using System.Threading.Channels;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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

        try
        {
            var deployment = await db.Deployments
                .Include(d => d.Release)
                    .ThenInclude(r => r.Project)
                .Include(d => d.Environment)
                .Include(d => d.Target)
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
                ct).ConfigureAwait(false);

            // ── 2. Build Octostache dictionary ───────────────────────────────
            // Scalar (String + Sensitive) variables go straight into Octostache.
            // StringArray variables are expanded as both VarName (comma-joined)
            // and VarName[0], VarName[1], … for indexed / #{each} access.
            var varDict = new VariableDictionary();

            // Standard Octopus variables always available in scripts.
            varDict["Octopus.Environment.Name"] = deployment.Environment.Name;
            varDict["Octopus.Deployment.Id"] = deploymentId.ToString();

            var flatVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Octopus.Environment.Name"] = deployment.Environment.Name,
                ["Octopus.Deployment.Id"] = deploymentId.ToString(),
            };

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

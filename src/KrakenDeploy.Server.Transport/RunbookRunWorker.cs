using System.Text.Json;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Deployments;
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
/// Background service that reads runbook-run IDs from the <see cref="RunbookRunChannel"/>,
/// resolves variables, builds a <see cref="DeploymentPlan"/> (using the RunbookRun ID
/// as the plan's DeploymentId), and sends it to the target agent.
/// The agent is unaware of the distinction — it executes the plan and calls back the
/// same <see cref="AgentHub"/> methods. The hub tries both tables on every callback.
/// </summary>
public sealed class RunbookRunWorker(
    RunbookRunChannel queue,
    IAgentConnectionRegistry registry,
    IHubContext<AgentHub, IAgentHubClient> agentHub,
    IServiceScopeFactory scopeFactory,
    ILogger<RunbookRunWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            _ = DispatchAsync(runId, stoppingToken);
        }
    }

    private async Task DispatchAsync(Guid runId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var variableService = scope.ServiceProvider.GetRequiredService<VariableService>();
        var serverBaseUrl = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["Server:BaseUrl"];

        try
        {
            var run = await db.RunbookRuns
                .Include(r => r.Runbook)
                    .ThenInclude(rb => rb.Project)
                .Include(r => r.Environment)
                .Include(r => r.Target)
                .Include(r => r.Tenant)
                .FirstOrDefaultAsync(r => r.Id == runId, ct)
                .ConfigureAwait(false);

            if (run is null)
            {
                logger.LogWarning("RunbookRunWorker: run {Id} not found.", runId);
                return;
            }

            if (run.TargetId is null)
            {
                await FailAsync(db, run, "No target assigned to runbook run.", ct).ConfigureAwait(false);
                return;
            }

            var connectionId = registry.GetConnectionId(run.TargetId.Value);
            if (connectionId is null)
            {
                await FailAsync(db, run, "Target is offline.", ct).ConfigureAwait(false);
                return;
            }

            if (run.ProcessSnapshot.Count == 0)
            {
                await FailAsync(db, run, "Runbook has no steps.", ct).ConfigureAwait(false);
                return;
            }

            // ── Resolve variables ────────────────────────────────────────────
            var targetRoles = run.Target?.Roles ?? [];
            var rawVars = await variableService.ResolveAsync(
                run.Runbook.ProjectId,
                run.EnvironmentId,
                run.TargetId,
                targetRoles,
                run.TenantId,
                ct).ConfigureAwait(false);

            // ── Build Octostache dictionary ───────────────────────────────────
            var varDict = new VariableDictionary();

            var systemVars = OctopusSystemVariablesBuilder.BuildForRunbookRun(
                run,
                run.Runbook,
                run.Runbook.Project,
                run.Environment,
                run.Target,
                run.Tenant,
                run.ProcessSnapshot,
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
                    try
                    {
                        var items = JsonSerializer.Deserialize<string[]>(value) ?? [];
                        arrayVars[name] = items;
                        var joined = string.Join(", ", items);
                        flatVars[name] = joined;
                        varDict[name] = joined;
                        for (var i = 0; i < items.Length; i++)
                        {
                            varDict[$"{name}[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"] = items[i];
                        }
                        continue;
                    }
                    catch (JsonException) { }
                }
                flatVars[name] = value;
                varDict[name] = value;
            }

            // ── Build plan ───────────────────────────────────────────────────
            var steps = run.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .Select((s, i) => new DeploymentStepPlan(
                    Index: i,
                    Name: s.Name,
                    StepType: s.StepType,
                    PackageId: s.PackageId,
                    PackageVersion: s.PackageVersion,
                    Config: SubstituteConfig(s.Config, varDict)))
                .ToArray();

            // The plan uses RunbookRun.Id as DeploymentId — AgentHub resolves both tables.
            var plan = new DeploymentPlan(
                DeploymentId: run.Id,
                EnvironmentName: run.Environment.Name,
                Steps: steps,
                Variables: flatVars,
                ArrayVariables: arrayVars);

            run.Status = DeploymentStatus.Running;
            run.StartedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Dispatching runbook run {RunId} ({Runbook}) to connection {Conn}.",
                runId, run.Runbook.Name, connectionId);

            await agentHub.Clients.Client(connectionId)
                .RunDeploymentAsync(plan)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unhandled error dispatching runbook run {RunId}.", runId);

            await using var errorScope = scopeFactory.CreateAsyncScope();
            var errorDb = errorScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            var run = await errorDb.RunbookRuns.FindAsync([runId], ct).ConfigureAwait(false);
            if (run is not null)
            {
                await FailAsync(errorDb, run, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, string> SubstituteConfig(
        Dictionary<string, string> config, VariableDictionary vars)
    {
        if (config.Count == 0)
        {
            return config;
        }
        return config.ToDictionary(
            kv => kv.Key,
            kv => vars.Evaluate(kv.Value) ?? kv.Value);
    }

    private static async Task FailAsync(
        KrakenDbContext db, Core.Domain.Runbooks.RunbookRun run, string reason, CancellationToken ct)
    {
        run.Status = DeploymentStatus.Failed;
        run.CompletedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

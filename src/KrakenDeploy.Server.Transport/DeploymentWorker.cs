using System.Threading.Channels;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Background service that reads deployment IDs from the in-process channel,
/// resolves the target agent's SignalR connection, and sends the
/// <see cref="DeploymentPlan"/> to the agent.
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

            // Build the deployment plan from the release snapshot.
            var steps = deployment.Release.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .Select((s, i) => new DeploymentStepPlan(
                    Index: i,
                    Name: s.Name,
                    StepType: s.StepType,
                    PackageId: s.PackageId,
                    PackageVersion: s.PackageVersion,
                    Config: s.Config))
                .ToArray();

            var plan = new DeploymentPlan(
                DeploymentId: deployment.Id,
                EnvironmentName: deployment.Environment.Name,
                Steps: steps,
                Variables: new Dictionary<string, string>());

            // Transition to Running before sending so the UI updates immediately.
            deployment.Status = DeploymentStatus.Running;
            deployment.StartedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Dispatching deployment {DeploymentId} to connection {ConnectionId}.",
                deploymentId, connectionId);

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

    private static async Task FailAsync(
        KrakenDbContext db, Deployment deployment, string reason, CancellationToken ct)
    {
        deployment.Status = DeploymentStatus.Failed;
        deployment.CompletedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

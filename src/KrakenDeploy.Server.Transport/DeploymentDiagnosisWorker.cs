using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services.Ai.Diagnosis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// M11.C — background service that drains the
/// <see cref="DeploymentDiagnosisChannel"/> and runs an AI diagnosis for each
/// failed deployment the orchestrator dropped on it. Mirrors
/// <see cref="DeploymentWorker"/>'s shape (channel reader loop +
/// fire-and-forget per item) so diagnosis runs concurrently without holding
/// up the queue.
/// <para>
/// Strictly best-effort: <see cref="DeploymentDiagnosisService.DiagnoseAsync"/>
/// swallows AI-unavailable + transient errors internally; this worker adds a
/// catch-all so one bad item never tears down the loop.
/// </para>
/// </summary>
public sealed class DeploymentDiagnosisWorker(
    DeploymentDiagnosisChannel channel,
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentDiagnosisWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var deploymentId in channel.Reader.ReadAllAsync(stoppingToken))
        {
            _ = DiagnoseAsync(deploymentId, stoppingToken);
        }
    }

    private async Task DiagnoseAsync(Guid deploymentId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<DeploymentDiagnosisService>();
            await service.DiagnoseAsync(deploymentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DiagnoseAsync already swallows AI / assembly errors; this is the
            // last-resort net so a surprise (e.g. DI resolution) doesn't kill
            // the reader loop.
            logger.LogError(ex,
                "Unhandled error diagnosing deployment {DeploymentId}.", deploymentId);
        }
    }
}

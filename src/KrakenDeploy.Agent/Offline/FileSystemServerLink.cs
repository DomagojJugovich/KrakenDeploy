using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Contracts.Offline;

namespace KrakenDeploy.Agent.Offline;

/// <summary>
/// Offline <see cref="IServerLink"/>: there is no server to call, so log lines
/// are appended to <c>deployment-log.txt</c> and the aggregated outcome
/// (per-step success + captured output variables) is written to
/// <c>deployment-result.json</c> in the bundle directory. The operator uploads
/// that result back to the server to reconcile the deployment.
/// <para>
/// The control-plane members (register / heartbeat / status / push handlers)
/// are no-ops — the runner drives <c>DeploymentExecutor.ExecuteAsync</c>
/// directly rather than waiting for a server push. Cross-step output-variable
/// feed-forward needs nothing here: the executor accumulates outputs locally
/// across waves within the single ExecuteAsync call.
/// </para>
/// </summary>
/// <param name="planSteps">
/// The plan's steps, used at completion to record each step's real Required flag
/// and to mark condition-skipped steps (steps in the plan that never reported a
/// completion on a successful run) — so the offline Steps tab matches online.
/// </param>
/// <param name="bundleKey">
/// Per-target bundle key; the written result is HMAC-signed with it
/// (<see cref="OfflineResultSigner"/>) so the server can detect tampering on
/// upload.
/// </param>
public sealed class FileSystemServerLink(
    string bundleRoot,
    IReadOnlyList<DeploymentStepPlan> planSteps,
    byte[] bundleKey) : IServerLink
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _logPath = Path.Combine(bundleRoot, OfflineBundleLayout.LogFile);
    private readonly Lock _logLock = new();
    private readonly ConcurrentDictionary<int, OfflineStepResult> _steps = new();

    public bool IsConnected => true;

    public Task StartAsync(string serverUrl, string agentJwt, string? releaseId, CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RegisterAsync(AgentRegistrationRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task ReportStatusAsync(string status, CancellationToken ct) => Task.CompletedTask;
    public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct) => Task.CompletedTask;
    public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
    public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }

    public Task AppendLogAsync(Guid deploymentId, int stepIndex, string level, string message, CancellationToken ct)
    {
        _ = stepIndex; // offline log is a flat file; step attribution is added on result import
        var ts = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var line = $"{ts} | {level} | {message}{Environment.NewLine}";
        lock (_logLock)
        {
            File.AppendAllText(_logPath, line, Encoding.UTF8);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{level}] {message}"));
        return Task.CompletedTask;
    }

    public Task ReportStepCompletedAsync(
        Guid deploymentId,
        int stepIndex,
        string stepName,
        bool success,
        string? errorMessage,
        IReadOnlyDictionary<string, string> outputVariables,
        CancellationToken ct)
    {
        _steps[stepIndex] = new OfflineStepResult
        {
            StepIndex = stepIndex,
            StepName = stepName,
            Success = success,
            ErrorMessage = errorMessage,
            OutputVariables = new Dictionary<string, string>(outputVariables, StringComparer.OrdinalIgnoreCase),
        };
        return Task.CompletedTask;
    }

    public async Task CompleteDeploymentAsync(
        Guid deploymentId, bool success, string? errorMessage, CancellationToken ct)
    {
        var result = new OfflineDropResult
        {
            DeploymentId = deploymentId,
            Success = success,
            ErrorMessage = errorMessage,
            CompletedUtc = DateTimeOffset.UtcNow,
            Steps = BuildStepResults(success),
        };
        var json = JsonSerializer.Serialize(result, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);

        await File.WriteAllBytesAsync(
            Path.Combine(bundleRoot, OfflineBundleLayout.ResultFile), bytes, ct).ConfigureAwait(false);

        // Sign the result so the server can detect tampering on upload.
        var sig = OfflineResultSigner.Sign(bundleKey, bytes);
        await File.WriteAllBytesAsync(
            Path.Combine(bundleRoot, OfflineBundleLayout.ResultSignatureFile), sig, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles the reported step outcomes against the plan: fills each
    /// reported step's real Required flag, and — on a successful run (no Required
    /// failure, so every wave ran) — adds a Skipped entry for any plan step that
    /// never reported (its Run Condition skipped it). On a failed run, steps in
    /// not-yet-reached waves are left out rather than mislabelled as skipped.
    /// </summary>
    private List<OfflineStepResult> BuildStepResults(bool success)
    {
        var requiredByIndex = planSteps.ToDictionary(s => s.Index, s => s.Required);
        var steps = new List<OfflineStepResult>(planSteps.Count);

        foreach (var plan in planSteps.OrderBy(s => s.Index))
        {
            var required = requiredByIndex.TryGetValue(plan.Index, out var r) && r;
            if (_steps.TryGetValue(plan.Index, out var reported))
            {
                steps.Add(reported with { Required = required, Skipped = false });
            }
            else if (success)
            {
                // Never reported on a successful run ⇒ condition-skipped.
                steps.Add(new OfflineStepResult
                {
                    StepIndex = plan.Index,
                    StepName = plan.AccumulatorKey ?? plan.Name,
                    Success = true,
                    Skipped = true,
                    Required = required,
                });
            }
        }
        return steps;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

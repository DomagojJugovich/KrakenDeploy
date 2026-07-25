using System.Collections.Concurrent;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.SignalR;

namespace KrakenDeploy.Server.Data.Tests.OrchestratorHarness;

/// <summary>
/// Pluggable response a <see cref="FakeAgent"/> returns for a step
/// dispatched by the orchestrator. Mirrors what the real agent would
/// stream back via <c>AgentHub.ReportStepCompletedAsync</c> +
/// <c>AgentHub.CompleteDeploymentAsync</c> at the end of the wave.
/// </summary>
public sealed record FakeStepResponse(
    bool Success,
    string? ErrorMessage = null,
    IReadOnlyDictionary<string, string>? Outputs = null,
    // B4 — subset of Outputs keys the (fake) agent flags sensitive (T0-6).
    IReadOnlyCollection<string>? SensitiveOutputs = null)
{
    public static FakeStepResponse Ok { get; } = new(Success: true);
    public static FakeStepResponse Fail(string reason) => new(Success: false, ErrorMessage: reason);
}

/// <summary>
/// One configured fake agent. The orchestrator dispatches sub-plans to a
/// connection id; the harness routes that to the matching agent via the
/// shared <see cref="IAgentConnectionRegistry"/>. Per-step responses are
/// keyed by step name with a default fallback for steps with no explicit
/// rule.
/// <para>
/// <strong>Synchronous simulation</strong>: when the orchestrator calls
/// <c>RunDeploymentAsync</c>, the agent walks the wave's steps inline,
/// records per-step boundaries via <see cref="IPendingSubPlanRegistry.RecordStepResult"/>,
/// and resolves the TCS via <see cref="IPendingSubPlanRegistry.TryResolve"/>
/// before <c>RunDeploymentAsync</c> returns. The orchestrator's
/// <c>await tcs.Task</c> then resolves immediately — no Task.Delay games
/// needed.
/// </para>
/// </summary>
public sealed class FakeAgent
{
    public Guid TargetId { get; init; }
    public required string ConnectionId { get; init; }

    /// <summary>Per-step-name override. First match wins; falls back to
    /// <see cref="DefaultResponse"/>.</summary>
    public Dictionary<string, FakeStepResponse> StepResponses { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeStepResponse DefaultResponse { get; set; } = FakeStepResponse.Ok;

    /// <summary>When non-null, the agent goes "offline" after this many wave
    /// dispatches (the harness removes its registry entry). Lets tests cover
    /// mid-deployment offline drop-outs.</summary>
    public int? GoOfflineAfterWaves { get; set; }

    /// <summary>B4 — every plan this agent received, in dispatch order. Lets
    /// tests assert what a LATER wave's sub-plan carried (merged output
    /// variables, extended sensitive-name list).</summary>
    public List<DeploymentPlan> ReceivedPlans { get; } = [];

    /// <summary>B6 — every CancelDeploymentAsync push this agent received.</summary>
    public List<(Guid TaskId, string? Reason)> CancelPushes { get; } = [];

    /// <summary>B3 — the agent receives the wave, drops its connection and
    /// reports NOTHING (crashed mid-execution). The wave's TCS stays pending;
    /// the worker's disconnect monitor must cancel it after the grace.</summary>
    public bool VanishBeforeReporting { get; set; }

    /// <summary>B3 — the agent receives the wave, STAYS connected and reports
    /// nothing (hung script, default TimeoutSeconds=0). The wave's TCS stays
    /// pending; the server-side wave deadline must fire. It DOES report execution
    /// started first: a hung script is one that took the machine slot and then
    /// stalled, so the F2 execution budget (not the queue backstop) must reap it.
    /// </summary>
    public bool NeverReport { get; set; }

    /// <summary>
    /// F2 — the agent receives the wave, stays connected, and never even reports
    /// execution start (it never acquires its machine slot — wedged behind a
    /// non-cooperative predecessor). Only the DISPATCH-TIME backstop ceiling can
    /// reap this, which is what keeps B3's always-armed invariant true.
    /// </summary>
    public bool NeverAcquireMachineSlot { get; set; }

    /// <summary>
    /// F2 — the agent QUEUES the received sub-plan behind another task on the same
    /// machine for this long, then reports execution start and runs the wave
    /// normally. <c>RunDeploymentAsync</c> returns immediately (as the real hub push
    /// does) and the work continues on a detached task, so the orchestrator is
    /// genuinely parked on its TCS while the queue wait elapses — that is the only
    /// way this exercises the deadline rather than the push.
    /// </summary>
    public TimeSpan? QueueBeforeExecuting { get; set; }

    /// <summary>
    /// F2 — how long the wave takes AFTER the agent has reported gate acquisition.
    /// Distinct from <see cref="QueueBeforeExecuting"/> on purpose: with zero work
    /// time a test cannot tell "the budget is measured from acquisition" from "the
    /// budget is merely larger than the queue wait", because any deadline past the
    /// queue wait passes. Real work time is what makes the arming point observable.
    /// </summary>
    public TimeSpan? WorkAfterAcquiring { get; set; }

    /// <summary>Optional callback invoked at the end of each
    /// <c>RunDeploymentAsync</c> — after the wave's steps are recorded + the TCS
    /// is resolved — receiving the 1-based wave count. Lets a test mutate
    /// external state (e.g. cancel the deployment) between waves so the
    /// orchestrator's next-boundary check observes it before the next wave.</summary>
    public Func<int, Task>? AfterWaveAsync { get; set; }

    internal int WaveCount;

    public FakeStepResponse ResponseFor(string stepName)
        => StepResponses.TryGetValue(stepName, out var r) ? r : DefaultResponse;
}

/// <summary>
/// Fake <see cref="IHubContext{THub, T}"/> for orchestrator E2E tests.
/// Returns a <see cref="FakeAgentClient"/> per <c>Client(connectionId)</c>
/// call; the client simulates the agent's per-step boundary reports +
/// final completion against the shared
/// <see cref="IPendingSubPlanRegistry"/>.
/// </summary>
internal sealed class FakeAgentHubContext : IHubContext<AgentHub, IAgentHubClient>
{
    private readonly FakeHubClients _clients;

    public FakeAgentHubContext(
        IPendingSubPlanRegistry subPlans,
        IAgentConnectionRegistry connectionRegistry,
        ConcurrentDictionary<Guid, FakeAgent> agentsByTargetId)
    {
        _clients = new FakeHubClients(subPlans, connectionRegistry, agentsByTargetId);
    }

    public IHubClients<IAgentHubClient> Clients => _clients;
    public IGroupManager Groups => throw new NotSupportedException(
        "FakeAgentHubContext: groups aren't used by the orchestrator's dispatch path.");
}

internal sealed class FakeHubClients(
    IPendingSubPlanRegistry subPlans,
    IAgentConnectionRegistry connectionRegistry,
    ConcurrentDictionary<Guid, FakeAgent> agentsByTargetId)
    : IHubClients<IAgentHubClient>
{
    public IAgentHubClient All => throw NotUsed();
    public IAgentHubClient AllExcept(IReadOnlyList<string> excluded) => throw NotUsed();

    public IAgentHubClient Client(string connectionId)
    {
        // Caller asked for a connection id; the orchestrator's offline-
        // detection path SHOULD have caught a missing one before calling
        // Client(), so reaching here with an unknown id means a harness
        // misconfiguration — fail loud so tests don't pass on a typo.
        var targetId = connectionRegistry.GetTargetId(connectionId)
            ?? throw new InvalidOperationException(
                $"FakeAgentHubContext: no fake agent registered for connection '{connectionId}'.");
        if (!agentsByTargetId.TryGetValue(targetId, out var agent))
        {
            throw new InvalidOperationException(
                $"FakeAgentHubContext: connection {connectionId} maps to target " +
                $"{targetId} but no FakeAgent is configured for that target.");
        }
        return new FakeAgentClient(agent, subPlans, connectionRegistry);
    }

    public IAgentHubClient Clients(IReadOnlyList<string> connectionIds) => throw NotUsed();
    public IAgentHubClient Group(string groupName) => throw NotUsed();
    public IAgentHubClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => throw NotUsed();
    public IAgentHubClient Groups(IEnumerable<string> groupNames) => throw NotUsed();
    public IAgentHubClient Groups(IReadOnlyList<string> groupNames) => throw NotUsed();
    public IAgentHubClient User(string userId) => throw NotUsed();
    public IAgentHubClient Users(IEnumerable<string> userIds) => throw NotUsed();
    public IAgentHubClient Users(IReadOnlyList<string> userIds) => throw NotUsed();

    private static NotSupportedException NotUsed()
        => new("FakeAgentHubContext: orchestrator only uses Clients.Client(connectionId).");
}

/// <summary>
/// The per-connection client the orchestrator calls
/// <see cref="IAgentHubClient.RunDeploymentAsync"/> on. Simulates the agent
/// inline: walks the plan's steps, calls RecordStepResult per step, then
/// TryResolve with the aggregate outcome. Synchronous wrt the orchestrator's
/// pending TCS so <c>await tcs.Task</c> resolves immediately.
/// </summary>
internal sealed class FakeAgentClient(
    FakeAgent agent,
    IPendingSubPlanRegistry subPlans,
    IAgentConnectionRegistry connectionRegistry)
    : IAgentHubClient
{
    public Task PingAsync() => Task.CompletedTask;

    /// <summary>B6 — records the push on the <see cref="FakeAgent"/> so tests can
    /// assert the orchestrator/cancel service actually notified the agent. The
    /// fake runs waves synchronously inside RunDeploymentAsync, so there is
    /// nothing in flight to abort here.</summary>
    public Task CancelDeploymentAsync(Guid taskId, string? reason)
    {
        agent.CancelPushes.Add((taskId, reason));
        return Task.CompletedTask;
    }

    public Task RunAdhocScriptAsync(KrakenDeploy.Contracts.Adhoc.AdhocScriptCommand command)
    {
        // The orchestrator-harness covers deployments only; adhoc dispatch is
        // exercised by AdhocDispatcherTests with its own fake pusher.
        ArgumentNullException.ThrowIfNull(command);
        return Task.CompletedTask;
    }

    public async Task RunDeploymentAsync(DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // B4 — record what this wave's sub-plan actually carried (merged
        // output variables, sensitive names) for test assertions.
        agent.ReceivedPlans.Add(plan);

        // F2 — a wedged agent never acquires its machine slot, so it never reports
        // execution start; only the dispatch-time backstop ceiling can reap it.
        if (agent.NeverAcquireMachineSlot)
        {
            return;
        }

        // F2 — queue behind another task on this machine, then execute. Detached so
        // the push returns immediately (as the real hub does) and the orchestrator
        // is genuinely parked on its TCS across the queue wait.
        if (agent.QueueBeforeExecuting is { } queueWait)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(queueWait).ConfigureAwait(false);
                await ExecuteWaveAsync(plan).ConfigureAwait(false);
            });
            return;
        }

        await ExecuteWaveAsync(plan).ConfigureAwait(false);
    }

    /// <summary>
    /// The wave body, from machine-slot acquisition onwards. Split out of
    /// <see cref="RunDeploymentAsync"/> so F2's queue simulation can run it on a
    /// detached task after the push has returned.
    /// </summary>
    private async Task ExecuteWaveAsync(DeploymentPlan plan)
    {
        // F2 — a real (contract v2) agent reports gate acquisition before its first
        // step; the server re-arms the wave deadline from it. Modelled here so the
        // harness exercises the same arming path production does.
        subPlans.TryMarkExecutionStarted(plan.DeploymentId, agent.TargetId, plan.DispatchId);

        // B3 failure-mode simulations: the plan was DELIVERED but the agent
        // never reports back. VanishBeforeReporting additionally drops the
        // connection (crash) so the worker's disconnect monitor fires;
        // NeverReport keeps it (hung script) so the wave deadline fires.
        if (agent.VanishBeforeReporting)
        {
            connectionRegistry.TryRemove(agent.ConnectionId, out _);
            return;
        }
        if (agent.NeverReport)
        {
            return;
        }

        // F2 — the wave's actual work, timed from gate acquisition. If the server
        // armed the deadline from DISPATCH instead, queue + work overruns it.
        if (agent.WorkAfterAcquiring is { } work)
        {
            await Task.Delay(work).ConfigureAwait(false);
        }

        // Per-step boundary reports — order matches plan.Steps so the
        // orchestrator's drain order matches what the real agent emits.
        var allSuccess = true;
        string? firstError = null;
        foreach (var step in plan.Steps)
        {
            var response = agent.ResponseFor(step.Name);
            // Echo plan.DispatchId exactly like the real DeploymentExecutor (B2).
            // B4: report against the ACCUMULATOR KEY like the real agent
            // (AccumulatorKey for ForEach iterations, display name otherwise).
            subPlans.RecordStepResult(
                plan.DeploymentId, agent.TargetId, plan.DispatchId,
                new SubPlanStepResult(
                    StepIndex:    step.Index,
                    StepName:     step.AccumulatorKey ?? step.Name,
                    Success:      response.Success,
                    ErrorMessage: response.ErrorMessage,
                    Outputs:      response.Outputs is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(response.Outputs, StringComparer.OrdinalIgnoreCase),
                    SensitiveOutputNames: response.SensitiveOutputs));
            if (!response.Success)
            {
                allSuccess = false;
                firstError ??= response.ErrorMessage ?? $"Step '{step.Name}' reported failure.";
            }
        }

        subPlans.RouteCompletion(
            plan.DeploymentId, agent.TargetId, plan.DispatchId,
            new SubPlanResult(allSuccess, firstError));

        // Optional offline-after-N-waves simulation: agent drops its
        // connection after THIS wave. The orchestrator's next wave will
        // see the target as offline and trigger Phase 3's drop-out.
        agent.WaveCount++;
        if (agent.GoOfflineAfterWaves is { } n && agent.WaveCount >= n)
        {
            connectionRegistry.TryRemove(agent.ConnectionId, out _);
        }

        // Between-wave hook: fires after this wave's TCS is resolved but before
        // the orchestrator advances, so a test can (e.g.) cancel the deployment
        // and have the next-boundary check observe it.
        if (agent.AfterWaveAsync is { } hook)
        {
            await hook(agent.WaveCount).ConfigureAwait(false);
        }
    }
}

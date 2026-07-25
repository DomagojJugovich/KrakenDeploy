namespace KrakenDeploy.Server.Data;

/// <summary>
/// B3 — engine resilience ceilings, bound from the <c>Engine</c> configuration
/// section. These exist so no orchestration path can wait on an agent forever:
/// pre-B3 a wave with the default step config (<c>TimeoutSeconds = 0</c>)
/// awaited its sub-plan TCS with no deadline, the worker's lease renewal kept
/// the B1 reconciler away (the process IS alive), the in-flight gauge stayed
/// up, and one dead agent blocked blue-green retirement indefinitely.
/// </summary>
public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>
    /// Server-side ceiling for one target-wave dispatch when no step in the
    /// wave configures an explicit <c>TimeoutSeconds</c>. An explicit step
    /// timeout is honoured as-is (even above this value) — the ceiling only
    /// replaces "unlimited". Applies per dispatch attempt (wave retries each
    /// get a fresh window). Server-side waves (incl. manual-intervention
    /// gates) are NOT subject to this ceiling.
    /// </summary>
    public TimeSpan MaxTargetWaveDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// F2 — how long a dispatched sub-plan may sit in the agent's machine execution
    /// queue (waiting for another task on that box to finish) before the server
    /// gives up on it.
    /// <para>
    /// The wave deadline itself arms when the agent reports it ACQUIRED the machine
    /// gate (<c>IAgentHubServer.ReportExecutionStartedAsync</c>), so queue time no
    /// longer burns the wave's budget. This value is the other half: the
    /// dispatch-time BACKSTOP is <c>wave budget + this</c>, which keeps B3's "the
    /// wave deadline is always armed" invariant true when the report never
    /// arrives — a wedged agent that stays connected but never executes again.
    /// </para>
    /// <para>
    /// Generous by default because it is only the slow path: an agent that actually
    /// disconnects is reaped by <see cref="AgentDisconnectWaveGrace"/> within
    /// minutes, and the agent escalates its own wedged gate
    /// (<c>DeploymentExecutor.WedgedGateAcquireTimeout</c>). Lower it only if you
    /// would rather fail a queued wave than let it wait behind long-running work.
    /// Non-positive falls back to <see cref="DefaultMaxTargetQueueWait"/>.
    /// </para>
    /// </summary>
    public TimeSpan MaxTargetQueueWait { get; set; } = DefaultMaxTargetQueueWait;

    /// <summary>Shipped default for <see cref="MaxTargetQueueWait"/>. Named so the
    /// consumer's non-positive fallback and this initializer cannot drift apart —
    /// the pre-existing ceilings restate their defaults as literals in
    /// <c>DeploymentWorker</c>, which is the trap this avoids repeating.</summary>
    public static readonly TimeSpan DefaultMaxTargetQueueWait = TimeSpan.FromHours(2);

    /// <summary>
    /// How long a mid-wave agent disconnect is tolerated before the wave is
    /// cancelled (resolves as a wave failure into the deployment's
    /// BestEffort/Atomic failure mode). Deliberately longer than the hub's
    /// 30 s offline-marking grace: the B2 agent reconnects with unbounded
    /// retry and FLUSHES buffered wave results on reconnect — cancelling too
    /// early discards work the flush would have delivered. Zero or negative
    /// disables the disconnect monitor (the wave deadline still applies).
    /// </summary>
    public TimeSpan AgentDisconnectWaveGrace { get; set; } = TimeSpan.FromMinutes(2);

    // D1 Phase 3: MaxRunbookRunDuration is gone — it was the drain ceiling for
    // legacy pre-D1 hand-off runbook runs (reconciler arm 4). Runbook runs hold
    // a live lease for the whole orchestration and are covered by the ordinary
    // lease-orphan reconcile + the B3 disconnect monitor, like deployments.

    /// <summary>
    /// B7 — how many deployment orchestrations this node runs concurrently
    /// (Octopus's task cap; same default of 5). Excess deployments stay queued
    /// on the dispatch channel until a slot frees. Each orchestration holds DB
    /// contexts, a log sequencer and per-target dispatch state for its whole
    /// duration — pre-B7 the worker fire-and-forgot every item unbounded.
    /// Non-positive falls back to the default. Runbook-run dispatch is NOT
    /// counted: the server-side hand-off is milliseconds (the run executes on
    /// the agent, serialized there by the agent's execution queue).
    /// <para>
    /// E3 note: a CHILD deployment spawned by an <c>Octopus.DeployRelease</c>
    /// step (<c>ParentTaskId != null</c>) does NOT take a slot — it is accounted
    /// for by its parent's slot, which the parent holds for the whole
    /// <c>WaitForChildAsync</c>. Without that bypass, capacity-many parents each
    /// waiting on a gate-starved child would deadlock the node.
    /// </para>
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 5;

    /// <summary>
    /// E3 — server-side ceiling for how long an <c>Octopus.DeployRelease</c> step
    /// waits on its child deployment (<c>DeployReleaseStepRunner.WaitForChildAsync</c>).
    /// The parent holds a <c>NodeTaskGate</c> slot for the whole wait; children
    /// bypass the gate so N parents on N children no longer deadlock, but a child
    /// that NEVER terminates would still pin the parent's slot forever — recovery
    /// today is a restart. This ceiling bounds that wait. It only replaces
    /// "unlimited": a step with an explicit <c>TimeoutSeconds</c> is honoured
    /// as-is (even above this — operator intent), same rule as
    /// <see cref="MaxTargetWaveDuration"/>. A ceiling hit classifies the step
    /// <c>TimedOut</c> (not generic Failed). Non-positive falls back to the
    /// shipped default.
    /// </summary>
    public TimeSpan MaxDeployReleaseWaitDuration { get; set; } = TimeSpan.FromHours(1);
}

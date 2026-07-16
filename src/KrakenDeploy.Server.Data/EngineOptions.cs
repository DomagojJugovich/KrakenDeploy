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
    /// How long a mid-wave agent disconnect is tolerated before the wave is
    /// cancelled (resolves as a wave failure into the deployment's
    /// BestEffort/Atomic failure mode). Deliberately longer than the hub's
    /// 30 s offline-marking grace: the B2 agent reconnects with unbounded
    /// retry and FLUSHES buffered wave results on reconnect — cancelling too
    /// early discards work the flush would have delivered. Zero or negative
    /// disables the disconnect monitor (the wave deadline still applies).
    /// </summary>
    public TimeSpan AgentDisconnectWaveGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Ceiling for an agent-owned runbook run (dispatched, lease released,
    /// hub finalizes on the agent's completion callback). A run still Running
    /// with its <c>StartedUtc</c> older than this never got its completion —
    /// the dispatch reconciler fails it. Raise for long maintenance runbooks.
    /// </summary>
    public TimeSpan MaxRunbookRunDuration { get; set; } = TimeSpan.FromHours(1);
}

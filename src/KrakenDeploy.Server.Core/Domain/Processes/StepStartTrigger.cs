namespace KrakenDeploy.Server.Core.Domain.Processes;

/// <summary>
/// Per-step "Start Trigger" knob (M14.4). Decides whether this step
/// waits for the previous step to complete or runs in parallel with it.
///
/// <para>
/// Mirrors Octopus's <c>StartTrigger</c> top-level field on the
/// deployment process step. Octopus's documented values are exactly
/// <c>StartAfterPrevious</c> and <c>StartWithPrevious</c>.
/// </para>
///
/// <para>
/// Pre-M14.4 behaviour is preserved by the <see cref="StartAfterPrevious"/>
/// default — every step waits for its predecessor, same as today. The
/// orchestrator's wave-partitioning consumes this field; the agent
/// never sees it (waves are pre-flattened server-side).
/// </para>
/// </summary>
public enum StepStartTrigger
{
    /// <summary>Default. Step waits for all previous steps to complete
    /// before starting.</summary>
    StartAfterPrevious = 0,

    /// <summary>Step runs in parallel with the previous step. Chained
    /// <c>StartWithPrevious</c> steps form a wave that runs together;
    /// the wave finishes when its slowest step completes.</summary>
    StartWithPrevious  = 1,
}

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
    /// early discards work the flush would have delivered. It must be greater
    /// than 30 seconds and shorter than the wave duration plus queue wait.
    /// </summary>
    public TimeSpan AgentDisconnectWaveGrace { get; set; } = TimeSpan.FromMinutes(2);

    // D1 Phase 3: MaxRunbookRunDuration is gone — it was the drain ceiling for
    // legacy pre-D1 hand-off runbook runs (reconciler arm 4). Runbook runs hold
    // a live lease for the whole orchestration and are covered by the ordinary
    // lease-orphan reconcile + the B3 disconnect monitor, like deployments.

    /// <summary>
    /// B7 — how many deployment orchestrations this node runs concurrently
    /// (startup snapshot, default 20). Excess deployments stay queued
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
    public int MaxConcurrentTasks { get; set; } = 20;

    /// <summary>
    /// Default maximum number of targets dispatched concurrently in a target
    /// wave. A valid explicit rolling-group <c>MaxParallelism</c> overrides this
    /// value. Waves without a valid explicit rolling cap are split into
    /// sequential batches of this size.
    /// </summary>
    public int DefaultTargetWaveMaxParallelism { get; set; } = 10;

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

    /// <summary>
    /// WP3 — how long a manual-intervention gate waits for a human before it
    /// auto-fails, when the step itself does not set
    /// <c>Kraken.Action.Manual.TimeoutHours</c>. Three days by default: long enough
    /// to span a weekend plus a public holiday, which is the realistic worst case for
    /// a state-sector change-approval board, and short enough that a forgotten gate
    /// does not hold its <c>(project, environment, tenant)</c> slot indefinitely
    /// (a <c>Paused</c> task is in-flight for F1 — see
    /// <c>DeploymentStatusExtensions.InFlightAfterClaim</c>).
    /// <para>
    /// Expiry fails the task exactly like a rejection, cleanup steps included.
    /// Must be POSITIVE — <c>TimeSpan.Zero</c> is refused at startup (WP3-b). It used
    /// to disable the default so gates waited forever, which is unsafe: a gate with no
    /// expiry is skipped by the timeout sweeper while its task keeps holding the F1
    /// <c>(project, environment, tenant)</c> key, so one unanswered gate blocks every
    /// later release of that project + environment. Rejecting a per-step <c>0</c> but
    /// accepting a server-wide one would only move the denial-of-release into a config
    /// file. Raise the value instead of disabling it.
    /// </para>
    /// <para>
    /// <b>F3 breadcrumb:</b> this ships as a config-file knob because F3 (the Engine
    /// settings document) has not landed yet — the same route <c>MaxTargetQueueWait</c>
    /// took in F2. Fold it into that document when F3 lands; house rule 11.
    /// </para>
    /// </summary>
    public TimeSpan DefaultInterventionTimeout { get; set; } = TimeSpan.FromHours(72);

    /// <summary>
    /// WP3-b — the HARD backstop for an <c>Octopus.DeployRelease</c> step whose child
    /// deployment parks at a manual-intervention gate. Default 7 days.
    /// <para>
    /// <see cref="MaxDeployReleaseWaitDuration"/> (1 h) stays the bound on how long the
    /// parent waits for a child that is actually <em>working</em>, and
    /// <c>WaitForChildAsync</c> now charges only NON-paused time against it — so a child
    /// waiting on a human no longer burns the parent's budget, while a genuinely hung
    /// child still fails in an hour. This value bounds the total anyway, so a child that
    /// pauses repeatedly cannot pin the parent's <c>NodeTaskGate</c> slot indefinitely.
    /// </para>
    /// <para>
    /// It must comfortably exceed <see cref="DefaultInterventionTimeout"/>: a gate that
    /// runs its full window and then continues is legitimate, and a backstop shorter than
    /// the approval window would fail the parent while the child was still answerable —
    /// the original defect (a 1 h parent ceiling against a 72 h gate).
    /// </para>
    /// </summary>
    public TimeSpan MaxDeployReleaseGatedWaitDuration { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// F2-followup 5 — fails the host at startup on an unusable <c>Engine</c> duration
/// instead of letting it surface as an opaque per-deployment crash.
/// <para>
/// The motivating misconfiguration is not exotic: <see cref="TimeSpan.Parse(string)"/>
/// reads a BARE NUMBER as days, so <c>"Engine:MaxTargetQueueWait": "4"</c> means four
/// DAYS, not four minutes. Combined with the F2 dispatch backstop
/// (<c>MaxTargetWaveDuration + MaxTargetQueueWait</c>) that pushes
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> past its
/// <c>uint.MaxValue - 1</c> ms limit, which throws
/// <see cref="ArgumentOutOfRangeException"/> on EVERY wave dispatch — every deployment
/// failing with a raw "Parameter 'delay'" message and nothing reaching any agent.
/// </para>
/// <para>
/// Two checks, because one does not cover the other. The magnitude ceiling catches
/// values big enough to be obviously wrong (and to overflow <c>CancelAfter</c>), but
/// the realistic typo — <c>"4"</c> — lands at four days, well INSIDE any ceiling
/// generous enough to be useful. So the bare-number form is rejected on sight, from
/// the raw configured string: once the binder has produced a <see cref="TimeSpan"/>,
/// <c>"4"</c> and <c>"4.00:00:00"</c> are indistinguishable, and the second one is
/// something an operator can only have written on purpose.
/// </para>
/// <para>
/// The consumers keep their non-positive fallbacks as belt-and-braces for tests and
/// other hosts that construct <see cref="EngineOptions"/> directly.
/// </para>
/// </summary>
public sealed class EngineOptionsValidator(IConfiguration configuration)
    : IValidateOptions<EngineOptions>
{
    /// <summary>Largest accepted value for any Engine duration. Well inside
    /// <c>CancelAfter</c>'s range, and generous enough for the slowest realistic
    /// deployment.</summary>
    public static readonly TimeSpan MaxAcceptedDuration = TimeSpan.FromDays(7);

    public ValidateOptionsResult Validate(string? name, EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var section = configuration.GetSection(EngineOptions.SectionName);
        var failures = new List<string>();

        Check(nameof(EngineOptions.MaxTargetWaveDuration), options.MaxTargetWaveDuration);
        Check(nameof(EngineOptions.MaxTargetQueueWait), options.MaxTargetQueueWait);
        Check(nameof(EngineOptions.AgentDisconnectWaveGrace), options.AgentDisconnectWaveGrace);
        Check(nameof(EngineOptions.MaxDeployReleaseWaitDuration),
            options.MaxDeployReleaseWaitDuration);
        Check(nameof(EngineOptions.MaxDeployReleaseGatedWaitDuration),
            options.MaxDeployReleaseGatedWaitDuration);
        // WP3-b: NOT allowZero. A zero default means gates never expire, and an
        // unexpiring gate parks its task on the F1 (project, environment, tenant) key
        // forever. Refusing the per-step 0 while accepting a server-wide one would just
        // move the denial-of-release into a config file.
        Check(nameof(EngineOptions.DefaultInterventionTimeout),
            options.DefaultInterventionTimeout);

        if (options.MaxConcurrentTasks <= 0)
        {
            failures.Add(
                $"Engine:{nameof(EngineOptions.MaxConcurrentTasks)} must be positive, " +
                $"got {options.MaxConcurrentTasks}.");
        }

        if (options.DefaultTargetWaveMaxParallelism <= 0)
        {
            failures.Add(
                $"Engine:{nameof(EngineOptions.DefaultTargetWaveMaxParallelism)} must be positive, " +
                $"got {options.DefaultTargetWaveMaxParallelism}.");
        }

        if (options.AgentDisconnectWaveGrace <= TimeSpan.FromSeconds(30))
        {
            failures.Add(
                $"Engine:{nameof(EngineOptions.AgentDisconnectWaveGrace)} must be greater than 30 seconds.");
        }
        if (options.AgentDisconnectWaveGrace >= options.MaxTargetWaveDuration + options.MaxTargetQueueWait)
        {
            failures.Add(
                $"Engine:{nameof(EngineOptions.AgentDisconnectWaveGrace)} must be less than " +
                $"Engine:{nameof(EngineOptions.MaxTargetWaveDuration)} plus " +
                $"Engine:{nameof(EngineOptions.MaxTargetQueueWait)}.");
        }
        if (options.MaxDeployReleaseGatedWaitDuration <= options.DefaultInterventionTimeout)
        {
            failures.Add(
                $"Engine:{nameof(EngineOptions.MaxDeployReleaseGatedWaitDuration)} must exceed " +
                $"Engine:{nameof(EngineOptions.DefaultInterventionTimeout)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

        void Check(string key, TimeSpan value)
        {
            var raw = section[key];
            if (raw is not null && IsBareNumber(raw)
                && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var configuredValue)
                && configuredValue == value)
            {
                failures.Add(
                    $"Engine:{key} is '{raw}', which TimeSpan binding reads as " +
                    $"{raw.Trim()} DAYS, not minutes or hours. Write the unit out as " +
                    "[d.]hh:mm:ss — '00:04:00' for four minutes, '04:00:00' for four " +
                    $"hours, '{raw.Trim()}.00:00:00' if you really did mean days.");
                return;
            }

            if (value <= TimeSpan.Zero)
            {
                failures.Add($"Engine:{key} must be a positive duration, got {value}.");
            }
            else if (value > MaxAcceptedDuration)
            {
                failures.Add(
                    $"Engine:{key} is {value}, above the {MaxAcceptedDuration} ceiling.");
            }
        }
    }

    /// <summary>Digits only (optionally signed) — the form
    /// <see cref="TimeSpan.Parse(string)"/> silently interprets as whole days.</summary>
    private static bool IsBareNumber(string raw)
    {
        var trimmed = raw.AsSpan().Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '-' or '+'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

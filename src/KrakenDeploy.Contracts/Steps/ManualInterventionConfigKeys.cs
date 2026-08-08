namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Canonical step type + config keys for a manual-intervention gate
/// (<c>Octopus.Manual</c>), shared by the server orchestrator, the step-schema UI
/// and the step package. Mirrors Octopus's <c>Octopus.Action.Manual.*</c> namespace
/// exactly for round-trip fidelity, plus one Kraken-native key
/// (<see cref="TimeoutHours"/>) Octopus has no equivalent for.
/// <para>
/// Sourced from
/// <a href="https://octopus.com/docs/projects/built-in-step-templates/manual-intervention-and-approvals">Octopus public docs</a>
/// (clean-room — not from Calamari source; see
/// <c>docs/architecture.md#step-execution-model</c>).
/// </para>
/// </summary>
public static class ManualInterventionConfigKeys
{
    private const string Prefix = "Octopus.Action.Manual.";

    /// <summary>The step type these keys configure.</summary>
    public const string StepType = "Octopus.Manual";

    /// <summary>
    /// Required (in both the Octopus and Kraken UIs). Markdown-formatted
    /// instructions shown to the human approver. Octostache <c>#{...}</c>
    /// placeholders are resolved when the task PAUSES, so the approver reads real
    /// project / environment / release values rather than the template.
    /// </summary>
    public const string Instructions = Prefix + "Instructions";

    /// <summary>
    /// Optional. Identifiers of the team(s) authorised to resolve the intervention.
    /// Octopus's serialiser uses commas or semicolons interchangeably depending on
    /// the surface (UI vs API); readers tolerate both. EMPTY means "anyone in the
    /// Space holding <c>InterruptionViewSubmitResponsible</c>" (Octopus semantics).
    /// <para>
    /// Kraken expects GUIDs referencing its own <c>teams</c>. A process imported
    /// from Octopus carries OCTOPUS team ids (e.g. <c>"teams-123"</c>), which cannot
    /// resolve — the orchestrator FAILS the gate rather than dropping them, because
    /// silently dropping an unresolvable approver list would widen the approver set
    /// from "these teams" to "anyone with the permission".
    /// </para>
    /// </summary>
    public const string ResponsibleTeamIds = Prefix + "ResponsibleTeamIds";

    /// <summary>
    /// Optional. Octopus blocks other deployments of the same project from
    /// progressing past this step while the intervention is unresolved. In Kraken
    /// this is INFORMATIONAL ONLY, because F1 already serializes deployments by
    /// <c>(project, environment, tenant)</c> unconditionally — a stronger and
    /// unavoidable guarantee. Surfaced in the task log for audit clarity.
    /// </summary>
    public const string BlockConcurrentDeployments = Prefix + "BlockConcurrentDeployments";

    /// <summary>
    /// Kraken-native, optional. Hours this gate waits for a human before it
    /// auto-fails. Blank falls back to <c>Engine:DefaultInterventionTimeout</c>
    /// (72 h). Octopus has no per-step equivalent.
    /// <para>
    /// <c>0</c> is REFUSED (WP3-b). It used to mean "wait forever", which was unsafe
    /// rather than merely unusual: <c>Paused</c> is in <c>InFlightAfterClaim</c>, so a
    /// parked task holds its <c>(project, environment, tenant)</c> F1 key, and the
    /// timeout sweeper skips a gate with no expiry. A step author holding only
    /// <c>ProcessEdit</c> could therefore block every future release of that
    /// project+environment indefinitely — a denial-of-release clearable only by
    /// someone with <c>TaskCancel</c>. Every gate must be bounded; raise the timeout
    /// (up to <see cref="MaxTimeoutHours"/>) instead of disabling it.
    /// </para>
    /// </summary>
    public const string TimeoutHours = "Kraken.Action.Manual.TimeoutHours";

    /// <summary>
    /// Legacy Kraken key used by an earlier internal step-template before alignment
    /// with the Octopus contract. Honoured on read for back-compat with any process
    /// already authored against the old shape.
    /// </summary>
    public const string LegacyInstructionsKey = "Instructions";

    /// <summary>
    /// Case-INSENSITIVE read of one of these keys from a step's config bag.
    /// <para>
    /// Mandatory for <see cref="ResponsibleTeamIds"/> and not merely tidy: a step's
    /// <c>Config</c> reaches the orchestrator as a plain jsonb-deserialised
    /// dictionary with the DEFAULT ordinal comparer, seeded from a caller-supplied
    /// <c>AddStepRequest.Config</c>. A casing miss on the approver key would return
    /// null, yielding an EMPTY responsible-team list — which means "anyone holding
    /// the respond permission". So a key typed
    /// <c>octopus.action.manual.responsibleteamids</c>, or normalised by an import,
    /// would silently widen the approver set from "these teams" to "everyone",
    /// while the step editor still displayed the restriction. Every other key in
    /// this class degrades harmlessly on a miss; this one fails OPEN, so all reads
    /// go through here.
    /// </para>
    /// </summary>
    public static string? Read(IReadOnlyDictionary<string, string> config, string key)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.TryGetValue(key, out var exact))
        {
            return exact;
        }
        foreach (var (k, v) in config)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Splits a raw <see cref="ResponsibleTeamIds"/> value into its individual
    /// tokens, tolerating commas and semicolons. Returns the RAW tokens — resolution
    /// to Kraken team GUIDs (and the hard failure on an unresolvable token) is the
    /// orchestrator's job, which needs the original text for its error message.
    /// </summary>
    public static List<string> ParseTeamTokens(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Largest accepted <see cref="TimeoutHours"/> value — one year. Above
    /// this an operator has typo'd, and the arithmetic starts to matter: an unclamped
    /// value overflows either <c>TimeSpan.FromHours</c> or the later
    /// <c>now + timeout</c>, throwing out of the gate and failing the deployment with
    /// a raw framework message instead of pausing.</summary>
    public const double MaxTimeoutHours = 8760;

    /// <summary>
    /// Reads <see cref="TimeoutHours"/>. Returns <c>null</c> when unset, unparseable,
    /// or out of range — callers fall back to <c>Engine:DefaultInterventionTimeout</c>,
    /// and process-save validation refuses the step so the fallback is never silent.
    /// <para>
    /// The accepted range is <c>(0, <see cref="MaxTimeoutHours"/>]</c> — STRICTLY
    /// positive. WP3-b closed the <c>0</c> case: it used to yield
    /// <see cref="TimeSpan.Zero"/> meaning "wait forever", which the gate turned into
    /// a NULL expiry that the timeout sweeper skips, leaving the task parked on its F1
    /// key indefinitely. Every gate must be bounded.
    /// </para>
    /// <para>
    /// Rejects — rather than clamping — anything not finite and in range.
    /// <see cref="NumberStyles.Float"/> with the invariant culture otherwise accepts
    /// <c>"Infinity"</c> and <c>"1e30"</c>, both of which pass a bare <c>&gt; 0</c>
    /// test and then throw <see cref="OverflowException"/> /
    /// <see cref="ArgumentOutOfRangeException"/> downstream (from
    /// <c>TimeSpan.FromHours</c> or the later <c>now + timeout</c>). <c>NaN</c> is
    /// excluded by <see cref="double.IsFinite"/> rather than by luck.
    /// </para>
    /// <para>
    /// Parsing is invariant on purpose — the value lives in a process document that
    /// must mean the same thing on every machine — so a Croatian operator typing
    /// <c>0,5</c> for thirty minutes does not parse. That no longer degrades silently:
    /// <c>ResponsibleTeamResolver.ValidateStepConfigAsync</c> refuses the save.
    /// </para>
    /// </summary>
    public static TimeSpan? ParseTimeout(string? raw)
        => double.TryParse(raw, System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out var hours)
           && double.IsFinite(hours)
           && hours > 0
           && hours <= MaxTimeoutHours
                ? TimeSpan.FromHours(hours)
                : null;

    /// <summary>True when the raw value is Octopus's <c>"True"</c> flag form.</summary>
    public static bool ParseBool(string? value)
        => value is not null && value.Equals("True", StringComparison.OrdinalIgnoreCase);
}

using System.Globalization;
using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Parses an Octopus <c>deploymentprocess</c> JSON document
/// (<c>GET /api/{spaceId}/deploymentprocesses/{processId}</c>) into a flat
/// list of <see cref="ParsedStep"/>s ready for upsert into a Kraken
/// <c>DeploymentProcess</c>.
/// <para>
/// Action <see cref="ParsedStep.Config"/> preserves the Octopus property bag
/// verbatim — <c>Octopus.Action.*</c> keys are not renamed. The step handler
/// at runtime decides whether to read Octopus or Kraken keys (dual-shape
/// strategy, see TASKS.md Phase B).
/// </para>
/// <para>
/// Out-of-LAUS-scope fields (<c>WorkerPoolId</c>, <c>Container</c>,
/// <c>Channels</c>, <c>Environments</c>/<c>ExcludedEnvironments</c>) are
/// reported as per-step warnings rather than imported, so the operator
/// notices when a process relies on a feature Kraken doesn't model yet.
/// </para>
/// </summary>
public static class OctopusDeploymentProcessImporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling          = JsonCommentHandling.Skip,
        AllowTrailingCommas          = true,
    };

    /// <summary>
    /// Parses the JSON. Throws <see cref="InvalidOperationException"/> on
    /// malformed JSON or a document shape that does not look like an Octopus
    /// deploymentprocess (i.e. missing the top-level <c>Steps</c> array).
    /// </summary>
    public static ParsedDeploymentProcess Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        OctopusDeploymentProcessDto? doc;
        try
        {
            doc = JsonSerializer.Deserialize<OctopusDeploymentProcessDto>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"JSON could not be parsed as an Octopus deploymentprocess: {ex.Message}", ex);
        }

        if (doc is null || doc.Steps is null)
        {
            throw new InvalidOperationException(
                "JSON does not look like an Octopus deploymentprocess — missing top-level 'Steps' array.");
        }

        var steps = new List<ParsedStep>();
        var warnings = new List<ImportDeploymentProcessWarning>();

        for (var i = 0; i < doc.Steps.Count; i++)
        {
            var s = doc.Steps[i];
            var label = string.IsNullOrWhiteSpace(s.Name)
                ? $"step[{i}]"
                : s.Name!;

            if (s.Actions is null || s.Actions.Count == 0)
            {
                warnings.Add(new(label, "Step has no actions — skipped."));
                continue;
            }

            // ── Single-action step → flat ParsedStep ────────────────────
            // The common case: Octopus's "1 step = 1 action" shape lands
            // as a leaf step verbatim. Step name dominates the action's
            // name here (matches the pre-M15 importer output exactly).
            if (s.Actions.Count == 1)
            {
                var leaf = ParseAction(s, s.Actions[0], label, warnings,
                    forcedStartTrigger: null);
                if (leaf is not null)
                {
                    if (!string.IsNullOrWhiteSpace(s.Name))
                    {
                        leaf = leaf with { Name = s.Name! };
                    }
                    steps.Add(leaf);
                }
                continue;
            }

            // ── Multi-action step → Kraken.StepGroup parent + children ──
            // M15: Octopus's multi-action shape is parent-with-children;
            // the importer creates a Kraken.StepGroup parent carrying
            // step-level metadata (TargetRoles, MaxParallelism for
            // M-RollingDeployments, future step-level keys) and emits
            // each action as a child. Octopus's default for multi-action
            // is parallel-on-same-target, so children 2..N get
            // StartTrigger=StartWithPrevious. Operators wanting sequential
            // execution flip the children to StartAfterPrevious in the
            // editor after import.
            var children = new List<ParsedStep>(s.Actions.Count);
            for (var ai = 0; ai < s.Actions.Count; ai++)
            {
                // Force StartTrigger on children 2..N to StartWithPrevious
                // to preserve Octopus's parallel-on-same-target default.
                var forcedTrigger = ai == 0
                    ? (StepStartTrigger?)null
                    : StepStartTrigger.StartWithPrevious;
                var child = ParseAction(s, s.Actions[ai], $"{label}/action[{ai}]",
                    warnings, forcedTrigger);
                if (child is not null)
                {
                    children.Add(child);
                }
            }
            if (children.Count == 0)
            {
                warnings.Add(new(label,
                    "Step has multiple actions but every one was unparseable; group skipped."));
                continue;
            }

            // Parent's Config carries every step-level property verbatim
            // — including Octopus.Action.MaxParallelism (reserved for
            // M-RollingDeployments) and any future step-level keys. The
            // step-level TargetRoles already resolved separately below.
            // s.Properties is already-normalised Dictionary<string, string>
            // (only the action's bag is JsonElement-typed).
            var parentConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (s.Properties is not null)
            {
                foreach (var (key, value) in s.Properties)
                {
                    // Octopus.Action.TargetRoles lives in step properties
                    // by convention; we surface it as TargetRoles on the
                    // ParsedStep itself, so don't double-store.
                    if (key.Equals("Octopus.Action.TargetRoles", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    parentConfig[key] = value;
                }
            }

            var parentRoles = ResolveTargetRoles(s.Properties);
            // Step-level Run Condition + Start Trigger from the top-level
            // step fields apply to the group as a whole.
            var parentCondition = ParseCondition(s.Condition);
            var parentStartTrigger = ParseStartTrigger(s.StartTrigger);
            var parentConditionVariableExpression = ResolveStepProperty(
                s.Properties, "Octopus.Step.ConditionVariableExpression");

            steps.Add(new ParsedStep(
                Name:                        string.IsNullOrWhiteSpace(s.Name) ? label : s.Name!,
                StepType:                    KrakenStepTypes.StepGroup,
                PackageId:                   string.Empty,
                TargetRoles:                 parentRoles,
                Config:                      parentConfig,
                Condition:                   parentCondition,
                ConditionVariableExpression: parentConditionVariableExpression,
                // Step groups themselves are Required by default; the
                // group's Required flag isn't a thing Octopus models —
                // it's per-action. Keep the Kraken default.
                Required:                    true,
                StartTrigger:                parentStartTrigger,
                Children:                    children));

            warnings.Add(new(label,
                $"Step has {s.Actions.Count} actions; imported as a Step Group " +
                $"with children running in parallel (StartTrigger=StartWithPrevious). " +
                $"Change children to StartAfterPrevious for sequential execution."));
        }

        return new ParsedDeploymentProcess(steps, warnings);
    }

    /// <summary>
    /// Parses a single Octopus action into a leaf <see cref="ParsedStep"/>.
    /// Returns null when the action is unusable (no ActionType, disabled,
    /// etc.) and emits a warning. <paramref name="forcedStartTrigger"/> lets
    /// the multi-action path override the action's natural trigger to
    /// preserve Octopus's parallel-on-same-target default for children 2..N.
    /// </summary>
    private static ParsedStep? ParseAction(
        OctopusStepDto s,
        OctopusActionDto a,
        string label,
        List<ImportDeploymentProcessWarning> warnings,
        StepStartTrigger? forcedStartTrigger)
    {
        if (string.IsNullOrWhiteSpace(a.ActionType))
        {
            warnings.Add(new(label, "Action has no ActionType — skipped."));
            return null;
        }

        if (a.IsDisabled)
        {
            warnings.Add(new(label,
                "Action is disabled in the source process; Kraken does not yet model disabled steps — skipped."));
            return null;
        }

        // Target roles come from the step's Properties (Octopus stores them there,
        // not on the action).
        var roles = ResolveTargetRoles(s.Properties);

        // Primary package: Kraken's DeploymentStep.PackageId is a real package
        // logical name. Octopus uses "dummy" as a sentinel on package-less steps
        // (e.g. an IIS-only configure step). Strip the sentinel — the verbatim
        // value still lives in Config["Octopus.Action.Package.PackageId"] for
        // round-trip.
        var packageId = ResolvePrimaryPackageId(a.Packages);

        // Config: copy Octopus property bag verbatim. No key translation —
        // the handler reads whichever shape it understands. Octopus emits
        // sensitive values as JSON objects ({HasValue,NewValue,Hint}); these
        // are preserved as their JSON-text representation in Config so a
        // round-trip back to Octopus can re-emit the envelope.
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (a.Properties is not null)
        {
            foreach (var (key, element) in a.Properties)
            {
                config[key] = NormalisePropertyValue(element);
            }
        }

        // Out-of-scope fields — warn rather than silently drop.
        if (!string.IsNullOrWhiteSpace(a.WorkerPoolId))
        {
            warnings.Add(new(label,
                $"Action targets worker pool '{a.WorkerPoolId}'; Kraken does not model worker pools — ignored."));
        }
        if (a.Container is not null && !string.IsNullOrWhiteSpace(a.Container.Image))
        {
            warnings.Add(new(label,
                $"Action runs inside container '{a.Container.Image}'; Kraken does not model container execution — ignored."));
        }
        if (a.TenantTags is { Count: > 0 })
        {
            warnings.Add(new(label,
                $"Action is scoped to tenant tags [{string.Join(", ", a.TenantTags)}]; per-step tenant-tag scoping is not yet propagated by the importer — step imported without scoping."));
        }
        if (a.Environments is { Count: > 0 } || a.ExcludedEnvironments is { Count: > 0 } ||
            a.Channels is { Count: > 0 })
        {
            warnings.Add(new(label,
                "Action carries Environments / ExcludedEnvironments / Channels scoping; per-step environment/channel scoping is not yet propagated by the importer — step imported without scoping."));
        }

        // ── M14 step-execution knobs ────────────────────────────────────
        // Read Octopus's per-step Run Condition + Start Trigger from the
        // top-level step fields (verified against argosy-process.json).
        // Action-level IsRequired carries the Required flag. AutoRetry's
        // MaximumCount lives in the action's Properties bag.
        // Variable-condition expressions live in the step's Properties bag.
        var condition = ParseCondition(s.Condition ?? a.Condition);
        var startTrigger = forcedStartTrigger ?? ParseStartTrigger(s.StartTrigger);
        var conditionVariableExpression = ResolveStepProperty(
            s.Properties, "Octopus.Step.ConditionVariableExpression");
        var maxRetries = ResolveActionRetryMaximumCount(a.Properties);
        // Octopus action's IsRequired is bool, defaults false. KrakenDeploy
        // defaults Required to true (preserves pre-M14 semantics where any
        // step failure aborted). The importer preserves Octopus's value
        // verbatim so a round-trip stays semantically identical.
        var required = a.IsRequired;

        // Prefer the action's name for child step (multi-action) so each
        // child gets its own identifiable name; fall back to step name +
        // label for the single-action case where step name dominates.
        var stepName = !string.IsNullOrWhiteSpace(a.Name)
            ? a.Name!
            : (string.IsNullOrWhiteSpace(s.Name) ? label : s.Name!);

        return new ParsedStep(
            Name:                        stepName,
            StepType:                    a.ActionType!,
            PackageId:                   packageId,
            TargetRoles:                 roles,
            Config:                      config,
            Condition:                   condition,
            ConditionVariableExpression: conditionVariableExpression,
            Required:                    required,
            MaxRetries:                  maxRetries,
            StartTrigger:                startTrigger);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static List<string> ResolveTargetRoles(Dictionary<string, string>? stepProperties)
    {
        if (stepProperties is null
            || !stepProperties.TryGetValue("Octopus.Action.TargetRoles", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Converts a JSON value into a Kraken-storable string. Plain string tokens
    /// are returned as-is. Object tokens (e.g. Octopus's
    /// <c>{HasValue,NewValue,Hint}</c> sensitive-value envelope) are re-serialised
    /// to JSON text so the envelope survives the round-trip through
    /// <c>Dictionary&lt;string,string&gt;</c>. Other primitives are stringified via
    /// <c>GetRawText()</c> so numbers / booleans / nulls keep their canonical form.
    /// </summary>
    private static string NormalisePropertyValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String                  => element.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _                                     => element.GetRawText(),
        };

    /// <summary>
    /// Parses Octopus's <c>Condition</c> string ("Success" / "Failure" /
    /// "Always" / "Variable") into the typed enum. Unknown / null values
    /// fall back to <see cref="StepCondition.Success"/> — the safe default
    /// that preserves the most common Octopus semantics.
    /// </summary>
    private static StepCondition ParseCondition(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "success"  => StepCondition.Success,
        "failure"  => StepCondition.Failure,
        "always"   => StepCondition.Always,
        "variable" => StepCondition.Variable,
        _          => StepCondition.Success,
    };

    /// <summary>
    /// Parses Octopus's <c>StartTrigger</c> string ("StartAfterPrevious" /
    /// "StartWithPrevious") into the typed enum. Unknown / null values
    /// fall back to <see cref="StepStartTrigger.StartAfterPrevious"/>.
    /// </summary>
    private static StepStartTrigger ParseStartTrigger(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "startwithprevious"  => StepStartTrigger.StartWithPrevious,
        "startafterprevious" => StepStartTrigger.StartAfterPrevious,
        _                    => StepStartTrigger.StartAfterPrevious,
    };

    /// <summary>
    /// Looks up a top-level property on the step's <c>Properties</c> bag.
    /// Returns null when the key is missing or the value is blank.
    /// </summary>
    private static string? ResolveStepProperty(
        Dictionary<string, string>? properties, string key)
    {
        if (properties is null || !properties.TryGetValue(key, out var value))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Resolves the action's auto-retry maximum count from the Octopus
    /// property bag. Key: <c>Octopus.Action.AutoRetry.MaximumCount</c>
    /// (integer string). Returns 0 when missing / unparseable — same as
    /// Octopus's "auto-retry disabled" default.
    /// </summary>
    private static int ResolveActionRetryMaximumCount(
        Dictionary<string, JsonElement>? properties)
    {
        if (properties is null
            || !properties.TryGetValue("Octopus.Action.AutoRetry.MaximumCount", out var element))
        {
            return 0;
        }
        var raw = NormalisePropertyValue(element);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return n < 0 ? 0 : n;
        }
        return 0;
    }

    private static string ResolvePrimaryPackageId(List<OctopusPackageDto>? packages)
    {
        if (packages is null || packages.Count == 0)
        {
            return string.Empty;
        }
        var first = packages[0];
        if (string.IsNullOrWhiteSpace(first.PackageId)
            || first.PackageId.Equals("dummy", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return first.PackageId;
    }

    // ── Internal DTOs (mirror the Octopus deploymentprocess JSON shape) ────

    private sealed record OctopusDeploymentProcessDto
    {
        public List<OctopusStepDto>? Steps { get; init; }
    }

    private sealed record OctopusStepDto
    {
        public string? Name { get; init; }
        public Dictionary<string, string>? Properties { get; init; }
        public List<OctopusActionDto>? Actions { get; init; }
        // M14 step-level fields (verified against argosy-process.json fixture):
        // top-level on the step JSON, NOT in the Properties bag.
        public string? Condition { get; init; }
        public string? StartTrigger { get; init; }
    }

    private sealed record OctopusActionDto
    {
        public string? Name { get; init; }
        public string? ActionType { get; init; }
        public bool IsDisabled { get; init; }
        // M14 action-level Required flag. Octopus stores it as IsRequired
        // on the action (verified against argosy-process.json). Defaults
        // to false in Octopus; KrakenDeploy preserves the source value.
        public bool IsRequired { get; init; }
        // Action-level Condition — Octopus stores per-action conditions on
        // multi-action steps. Used as a fallback when the step's top-level
        // Condition is absent.
        public string? Condition { get; init; }
        public string? WorkerPoolId { get; init; }
        public OctopusContainerDto? Container { get; init; }
        public List<string>? Environments { get; init; }
        public List<string>? ExcludedEnvironments { get; init; }
        public List<string>? Channels { get; init; }
        public List<string>? TenantTags { get; init; }
        public List<OctopusPackageDto>? Packages { get; init; }
        // JsonElement (not string) — Octopus emits sensitive properties as objects:
        // { "HasValue": true, "NewValue": "...", "Hint": "..." }
        public Dictionary<string, JsonElement>? Properties { get; init; }
    }

    private sealed record OctopusContainerDto
    {
        public string? Image { get; init; }
    }

    private sealed record OctopusPackageDto
    {
        public string? PackageId { get; init; }
        public string? FeedId { get; init; }
    }
}

/// <summary>
/// Result of parsing — a flat list of <see cref="ParsedStep"/>s plus
/// per-step warnings collected during the parse (skipped actions, ignored
/// out-of-scope fields, etc.).
/// </summary>
public sealed record ParsedDeploymentProcess(
    IReadOnlyList<ParsedStep> Steps,
    IReadOnlyList<ImportDeploymentProcessWarning> Warnings);

/// <summary>Single parsed step ready for upsert into a Kraken process.
/// M14 step-execution knobs are appended with defaults so older callers
/// (and importer paths that don't yet propagate them) continue to compile.
/// M15 adds <see cref="Children"/>: when an Octopus step carries multiple
/// actions, the importer emits a parent <see cref="ParsedStep"/> with
/// <c>StepType = Kraken.StepGroup</c> whose <see cref="Children"/> are
/// one <see cref="ParsedStep"/> per action.</summary>
public sealed record ParsedStep(
    string Name,
    string StepType,
    string PackageId,
    List<string> TargetRoles,
    Dictionary<string, string> Config,
    StepCondition Condition = StepCondition.Success,
    string? ConditionVariableExpression = null,
    bool Required = true,
    int MaxRetries = 0,
    int RetryDelaySeconds = 0,
    int TimeoutSeconds = 0,
    StepStartTrigger StartTrigger = StepStartTrigger.StartAfterPrevious,
    IReadOnlyList<ParsedStep>? Children = null);

/// <summary>Per-step warning surfaced during a deploymentprocess import.</summary>
public sealed record ImportDeploymentProcessWarning(string StepName, string Message);

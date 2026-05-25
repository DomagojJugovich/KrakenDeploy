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

            if (s.Actions.Count > 1)
            {
                warnings.Add(new(label,
                    $"Step has {s.Actions.Count} parallel actions; parallel actions are not yet supported — skipped."));
                continue;
            }

            var a = s.Actions[0];

            if (string.IsNullOrWhiteSpace(a.ActionType))
            {
                warnings.Add(new(label, "Action has no ActionType — skipped."));
                continue;
            }

            if (a.IsDisabled)
            {
                warnings.Add(new(label,
                    "Action is disabled in the source process; Kraken does not yet model disabled steps — skipped."));
                continue;
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
            var startTrigger = ParseStartTrigger(s.StartTrigger);
            var conditionVariableExpression = ResolveStepProperty(
                s.Properties, "Octopus.Step.ConditionVariableExpression");
            var maxRetries = ResolveActionRetryMaximumCount(a.Properties);
            // Octopus action's IsRequired is bool, defaults false. KrakenDeploy
            // defaults Required to true (preserves pre-M14 semantics where any
            // step failure aborted). The importer preserves Octopus's value
            // verbatim so a round-trip stays semantically identical.
            var required = a.IsRequired;

            steps.Add(new ParsedStep(
                Name:                        string.IsNullOrWhiteSpace(s.Name) ? a.Name ?? label : s.Name!,
                StepType:                    a.ActionType!,
                PackageId:                   packageId,
                TargetRoles:                 roles,
                Config:                      config,
                Condition:                   condition,
                ConditionVariableExpression: conditionVariableExpression,
                Required:                    required,
                MaxRetries:                  maxRetries,
                StartTrigger:                startTrigger));
        }

        return new ParsedDeploymentProcess(steps, warnings);
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
/// (and importer paths that don't yet propagate them) continue to compile.</summary>
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
    StepStartTrigger StartTrigger = StepStartTrigger.StartAfterPrevious);

/// <summary>Per-step warning surfaced during a deploymentprocess import.</summary>
public sealed record ImportDeploymentProcessWarning(string StepName, string Message);

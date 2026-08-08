using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Logging;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// B4 (T0-4) — the server-side mirror of the agent's within-dispatch output
/// accumulation. Online, every wave is its own sub-plan dispatch with a fresh
/// agent-side accumulator, so captured outputs never reached later waves:
/// <c>#{Octopus.Action[Step1].Output.X}</c> in a step-2 config field (and
/// <c>$OctopusParameters[...]</c> in scripts) silently resolved to nothing —
/// while offline drops and runbooks (whole plan in one dispatch) worked.
/// <para>
/// The orchestrator folds each wave's captured outputs in here and augments
/// every subsequent dispatch from it:
/// <list type="bullet">
/// <item><b>Per-target bags</b> — a target's later waves see ITS OWN captured
/// value for a machine-specific output (parity with the agent's accumulator;
/// same key shape via the shared <see cref="OutputVariableAccumulator"/>).</item>
/// <item><b>Server view</b> — server-side waves see a last-writer-wins fold
/// across targets (matching the existing parallel-collision semantics), plus
/// every server-step capture.</item>
/// <item><b>Run conditions</b> — folds stamp the merged keys into the
/// per-target and server-wave <see cref="VariableDictionary"/> bags so a
/// <c>Variable</c> run-condition can reference a prior step's output.</item>
/// <item><b>Sensitivity (T0-6)</b> — a sensitive output's merged key extends
/// the next plan's <see cref="DeploymentPlan.SensitiveVariableNames"/> (the
/// agent's redactor masks it in later waves' logs) and its VALUE is folded
/// into the server-side redactor immediately.</item>
/// </list>
/// Single-dispatch lifetime, orchestrator-thread use only (folds happen
/// between waves / after WhenAll, never concurrently).
/// </para>
/// </summary>
internal sealed class DeploymentOutputAccumulator
{
    private readonly IReadOnlyDictionary<Guid, DeploymentWorker.TargetDispatchContext> _contexts;
    private readonly SecretRedactor _serverRedactor;

    // stepKey -> (name -> value), per target — the shape the shared
    // OutputVariableAccumulator merges into a plan.
    private readonly Dictionary<Guid, Dictionary<string, Dictionary<string, string>>> _bagByTarget = new();
    // Fully-qualified merged key names (Octopus.Action[key].Output.name)
    // flagged sensitive, per target.
    private readonly Dictionary<Guid, HashSet<string>> _sensitiveByTarget = new();

    // Server-wave view: LWW fold across targets + all server captures.
    private readonly Dictionary<string, Dictionary<string, string>> _serverBag =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _serverSensitiveKeys =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Condition bag for server waves: a clone of the canonical
    /// target's dictionary that additionally accumulates output keys. A clone
    /// — not the canonical dictionary itself — because the canonical dict IS
    /// target[0]'s condition bag, and per-target isolation must hold for it.</summary>
    public VariableDictionary ServerConditionVarDict { get; }

    public DeploymentOutputAccumulator(
        IReadOnlyDictionary<Guid, DeploymentWorker.TargetDispatchContext> contexts,
        VariableDictionary canonicalVarDict,
        SecretRedactor serverRedactor)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(canonicalVarDict);
        ArgumentNullException.ThrowIfNull(serverRedactor);
        _contexts = contexts;
        _serverRedactor = serverRedactor;

        foreach (var targetId in contexts.Keys)
        {
            _bagByTarget[targetId] = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            _sensitiveByTarget[targetId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        ServerConditionVarDict = new VariableDictionary();
        foreach (var name in canonicalVarDict.GetNames())
        {
            ServerConditionVarDict[name] = canonicalVarDict[name];
        }
    }

    // ── Folds ───────────────────────────────────────────────────────────────

    /// <summary>Folds one target step's captured outputs (drained per-step
    /// report). <paramref name="stepKey"/> is the accumulator key the agent
    /// reported against (ForEach synthetic key or display name). Captures are
    /// folded regardless of step success — parity with the agent.</summary>
    public void RecordTargetStep(
        Guid targetId,
        string stepKey,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyCollection<string>? sensitiveNames)
    {
        if (outputs.Count == 0 || !_bagByTarget.TryGetValue(targetId, out var bag))
        {
            return;
        }

        FoldIntoBag(bag, stepKey, outputs);
        FoldIntoBag(_serverBag, stepKey, outputs);
        StampVarDict(_contexts[targetId].VarDict, stepKey, outputs);
        StampVarDict(ServerConditionVarDict, stepKey, outputs);
        RecordSensitive(stepKey, outputs, sensitiveNames,
            _sensitiveByTarget[targetId], _serverSensitiveKeys);
    }

    /// <summary>Folds one server-side step's captured outputs (B4 scope 2).
    /// Server steps are deployment-scoped, so every target's later waves see
    /// them, as does the server view itself.</summary>
    public void RecordServerStep(
        string stepKey,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyCollection<string>? sensitiveNames)
    {
        if (outputs.Count == 0)
        {
            return;
        }

        FoldIntoBag(_serverBag, stepKey, outputs);
        StampVarDict(ServerConditionVarDict, stepKey, outputs);
        foreach (var (targetId, bag) in _bagByTarget)
        {
            FoldIntoBag(bag, stepKey, outputs);
            StampVarDict(_contexts[targetId].VarDict, stepKey, outputs);
            RecordSensitive(stepKey, outputs, sensitiveNames,
                _sensitiveByTarget[targetId], _serverSensitiveKeys);
        }
        // Single-target-less edge (all targets dropped): still track sensitivity
        // for the server view.
        if (_bagByTarget.Count == 0)
        {
            RecordSensitive(stepKey, outputs, sensitiveNames,
                extraSet: null, _serverSensitiveKeys);
        }
    }

    // ── WP3 pause/resume ────────────────────────────────────────────────────

    /// <summary>
    /// Flattens the accumulated bags for the pause checkpoint. Only the per-target
    /// and server bags plus their sensitive key sets are exported —
    /// <see cref="ServerConditionVarDict"/> and the per-target
    /// <c>VarDict</c> stamps are pure functions of them and are replayed by
    /// <see cref="RestoreFrom"/>.
    /// </summary>
    public (List<CheckpointOutputBag> TargetOutputs, List<CheckpointOutputBag> ServerOutputs) Export()
    {
        var targetOutputs = new List<CheckpointOutputBag>();
        foreach (var (targetId, bag) in _bagByTarget)
        {
            var sensitive = _sensitiveByTarget[targetId];
            foreach (var (stepKey, values) in bag)
            {
                targetOutputs.Add(new CheckpointOutputBag(
                    targetId, stepKey,
                    new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
                    [.. values.Keys.Select(n => MergedKey(stepKey, n)).Where(sensitive.Contains)]));
            }
        }

        var serverOutputs = new List<CheckpointOutputBag>();
        foreach (var (stepKey, values) in _serverBag)
        {
            serverOutputs.Add(new CheckpointOutputBag(
                TargetId: null, stepKey,
                new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
                [.. values.Keys.Select(n => MergedKey(stepKey, n)).Where(_serverSensitiveKeys.Contains)]));
        }

        return (targetOutputs, serverOutputs);
    }

    /// <summary>
    /// Re-seeds the bags from a pause checkpoint and replays everything derived from
    /// them, so a resumed run's later waves see prior outputs exactly as an
    /// uninterrupted run would: the per-target and server run-condition
    /// dictionaries, and the server-side <see cref="SecretRedactor"/> (a later
    /// server step's environment carries sensitive values in plaintext, so a log
    /// line echoing one must still mask after a resume).
    /// <para>
    /// Bags for a target that is no longer in <c>contexts</c> are DROPPED, not
    /// resurrected — the caller has already reconciled the checkpoint's alive set
    /// against the task's current assignments.
    /// </para>
    /// </summary>
    public void RestoreFrom(
        IEnumerable<CheckpointOutputBag> targetOutputs,
        IEnumerable<CheckpointOutputBag> serverOutputs)
    {
        ArgumentNullException.ThrowIfNull(targetOutputs);
        ArgumentNullException.ThrowIfNull(serverOutputs);

        foreach (var entry in targetOutputs)
        {
            if (entry.TargetId is not { } targetId
                || !_bagByTarget.TryGetValue(targetId, out var bag))
            {
                continue;
            }
            FoldIntoBag(bag, entry.StepKey, entry.Values);
            StampVarDict(_contexts[targetId].VarDict, entry.StepKey, entry.Values);
            RestoreSensitive(entry, _sensitiveByTarget[targetId]);
        }

        foreach (var entry in serverOutputs)
        {
            FoldIntoBag(_serverBag, entry.StepKey, entry.Values);
            StampVarDict(ServerConditionVarDict, entry.StepKey, entry.Values);
            RestoreSensitive(entry, extraSet: null);
        }
    }

    /// <summary>Re-registers a checkpointed bag's sensitive merged keys and folds
    /// their VALUES back into the server redactor.</summary>
    private void RestoreSensitive(CheckpointOutputBag entry, HashSet<string>? extraSet)
    {
        foreach (var mergedKey in entry.SensitiveMergedKeys)
        {
            extraSet?.Add(mergedKey);
            _serverSensitiveKeys.Add(mergedKey);
        }
        if (entry.SensitiveMergedKeys.Length == 0)
        {
            return;
        }
        var sensitive = new HashSet<string>(entry.SensitiveMergedKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entry.Values)
        {
            if (sensitive.Contains(MergedKey(entry.StepKey, name)) && !string.IsNullOrEmpty(value))
            {
                _serverRedactor.Add([value]);
            }
        }
    }

    // ── Views ───────────────────────────────────────────────────────────────

    /// <summary>
    /// WP3-b — a clone of <see cref="ServerConditionVarDict"/> in which every SENSITIVE
    /// value is replaced by <see cref="SecretRedactor.Mask"/>, for rendering text that
    /// gets PERSISTED and shown to a wider audience than the variable itself (today:
    /// a manual-intervention gate's instructions).
    /// <para>
    /// Masking the value rather than redacting the rendered output is the only correct
    /// order. Redaction is an ordinal substring match on the raw secret, so any
    /// Octostache filter defeats it — <c>#{Db.Password | ToBase64}</c>, <c>| ToUpper</c>
    /// and <c>| Md5</c> all produce a string the redactor cannot recognise, and the
    /// transformed secret lands in cleartext. Filters cannot launder what was never in
    /// the dictionary, and this also covers indexed refs and indirection
    /// (<c>#{#{NameHolder}}</c>) that a token-text rewrite would miss.
    /// </para>
    /// <para>
    /// Both sources of sensitivity are covered: <paramref name="planSensitiveNames"/>
    /// (the canonical plan's declared sensitive variables) and the sensitive OUTPUT
    /// keys folded into the server bag during this run.
    /// </para>
    /// </summary>
    public VariableDictionary MaskedServerConditionVarDict(
        IReadOnlyCollection<string> planSensitiveNames)
    {
        ArgumentNullException.ThrowIfNull(planSensitiveNames);
        var sensitive = new HashSet<string>(planSensitiveNames, StringComparer.OrdinalIgnoreCase);
        sensitive.UnionWith(_serverSensitiveKeys);

        var masked = new VariableDictionary();
        foreach (var name in ServerConditionVarDict.GetNames())
        {
            masked[name] = sensitive.Contains(name)
                ? SecretRedactor.Mask
                : ServerConditionVarDict[name];
        }
        return masked;
    }

    /// <summary>Returns <paramref name="plan"/> with this target's accumulated
    /// outputs merged into <see cref="DeploymentPlan.Variables"/> (shared key
    /// shape) and sensitive merged keys appended to
    /// <see cref="DeploymentPlan.SensitiveVariableNames"/>. Returns the same
    /// instance when nothing has been captured yet (first wave).</summary>
    public DeploymentPlan AugmentPlanForTarget(Guid targetId, DeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_bagByTarget.TryGetValue(targetId, out var bag) || bag.Count == 0)
        {
            return plan;
        }

        var augmented = OutputVariableAccumulator.AugmentPlanWithPriorOutputs(plan, bag);

        var sensitive = _sensitiveByTarget[targetId];
        if (sensitive.Count > 0)
        {
            var names = new HashSet<string>(
                plan.SensitiveVariableNames ?? [], StringComparer.OrdinalIgnoreCase);
            names.UnionWith(sensitive);
            augmented = augmented with { SensitiveVariableNames = names };
        }

        return augmented;
    }

    /// <summary>Server-wave environment view: <paramref name="flatVars"/> plus
    /// the LWW-folded output keys. Server scripts read them via
    /// <c>$OctopusParameters[...]</c>/env exactly like agent-side scripts.</summary>
    public IReadOnlyDictionary<string, string> AugmentServerVariables(
        IReadOnlyDictionary<string, string> flatVars)
    {
        ArgumentNullException.ThrowIfNull(flatVars);
        if (_serverBag.Count == 0)
        {
            return flatVars;
        }

        var merged = new Dictionary<string, string>(flatVars, StringComparer.OrdinalIgnoreCase);
        foreach (var (stepKey, outputs) in _serverBag)
        {
            foreach (var (name, value) in outputs)
            {
                merged[MergedKey(stepKey, name)] = value;
            }
        }
        return merged;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string MergedKey(string stepKey, string name)
        => $"Octopus.Action[{stepKey}].Output.{name}";

    private static void FoldIntoBag(
        Dictionary<string, Dictionary<string, string>> bag,
        string stepKey,
        IReadOnlyDictionary<string, string> outputs)
    {
        if (!bag.TryGetValue(stepKey, out var perStep))
        {
            perStep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bag[stepKey] = perStep;
        }
        foreach (var (name, value) in outputs)
        {
            perStep[name] = value;
        }
    }

    private static void StampVarDict(
        VariableDictionary dict, string stepKey, IReadOnlyDictionary<string, string> outputs)
    {
        foreach (var (name, value) in outputs)
        {
            dict[MergedKey(stepKey, name)] = value;
        }
    }

    private void RecordSensitive(
        string stepKey,
        IReadOnlyDictionary<string, string> outputs,
        IReadOnlyCollection<string>? sensitiveNames,
        HashSet<string>? extraSet,
        HashSet<string> serverSet)
    {
        if (sensitiveNames is not { Count: > 0 })
        {
            return;
        }

        foreach (var name in sensitiveNames)
        {
            var key = MergedKey(stepKey, name);
            extraSet?.Add(key);
            serverSet.Add(key);

            // Fold the VALUE into the server-side redactor now — a later
            // server step's env carries it in plaintext, and any log line
            // echoing it must mask (the agent side masks via the plan's
            // SensitiveVariableNames + its own live fold).
            if (outputs.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                _serverRedactor.Add([value]);
            }
        }
    }
}

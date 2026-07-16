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

    // ── Views ───────────────────────────────────────────────────────────────

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

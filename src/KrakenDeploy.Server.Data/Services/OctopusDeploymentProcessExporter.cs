using System.Text.Json.Nodes;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Execution;
using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Inverse of <see cref="OctopusDeploymentProcessImporter"/>: serializes a
/// Kraken deployment process into the Octopus <c>deploymentprocess</c> JSON
/// shape, so an export can be re-imported by Kraken (round-trip) or pasted
/// into tooling that speaks the Octopus dialect.
/// <para>
/// Mapping mirrors the importer exactly: leaf step → single-action step with
/// roles in step <c>Properties["Octopus.Action.TargetRoles"]</c> and the
/// <c>Config</c> bag verbatim as action <c>Properties</c>; a
/// <c>Kraken.StepGroup</c> → multi-action step. Knobs the importer doesn't
/// read (<c>RetryDelaySeconds</c>, <c>TimeoutSeconds</c>) are emitted as
/// <c>Kraken.Action.*</c> extension keys — they survive re-import inside the
/// step's Config (not as the typed knob fields) and are reported as warnings.
/// </para>
/// </summary>
public static class OctopusDeploymentProcessExporter
{
    public static (JsonObject Process, List<ImportDeploymentProcessWarning> Warnings) Export(
        IReadOnlyList<ProcessStep> steps)
    {
        var warnings = new List<ImportDeploymentProcessWarning>();
        var childrenByParent = steps
            .Where(s => s.ParentStepId is not null)
            .GroupBy(s => s.ParentStepId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SortOrder).ToList());

        var stepsArr = new JsonArray();
        foreach (var root in steps.Where(s => s.ParentStepId is null).OrderBy(s => s.SortOrder))
        {
            if (root.StepType == KrakenStepTypes.StepGroup)
            {
                var actions = new JsonArray();
                CollectActions(root, childrenByParent, actions, warnings, root);
                if (actions.Count == 0)
                {
                    warnings.Add(new(root.Name, "Step group has no child steps — skipped."));
                    continue;
                }

                var stepObj = StepShell(root, omitConditionWhenDefault: true);
                // Parent Config carries the remaining step-level properties
                // verbatim (e.g. ForEach.IterationVariable/.IndexVariable) — merge
                // them into the step's Properties bag, where the importer reads
                // them back.
                var props = (JsonObject)stepObj["Properties"]!;
                foreach (var (key, value) in root.Config)
                {
                    props[key] ??= value;
                }
                // D3 — re-emit the promoted Step-Group flags from their typed
                // columns as the Octopus-compatible keys (emit-only-when-set,
                // mirroring the M14 knob emits in ActionObj). This is the ONLY
                // place these keys reappear in the Octopus dialect.
                if (root.MaxParallelism is > 0)
                {
                    props[KrakenStepGroupConfigKeys.MaxParallelism] ??=
                        root.MaxParallelism.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                }
                if (!string.IsNullOrWhiteSpace(root.ForEachCollection))
                {
                    props[KrakenStepGroupConfigKeys.ForEachCollection] ??= root.ForEachCollection;
                }
                if (root.ForEachParallel)
                {
                    props[KrakenStepGroupConfigKeys.ForEachParallel] ??= "true";
                }
                stepObj["Actions"] = actions;
                stepsArr.Add(stepObj);
            }
            else
            {
                var stepObj = StepShell(root, omitConditionWhenDefault: false);
                stepObj["Actions"] = new JsonArray(ActionObj(root, warnings));
                stepsArr.Add(stepObj);
            }
        }

        return (new JsonObject { ["Steps"] = stepsArr }, warnings);
    }

    /// <summary>
    /// Flattens a group's descendants into Octopus actions. Nested groups
    /// can't be represented (Octopus has exactly one step→actions level), so
    /// their leaves are flattened in document order with a warning.
    /// </summary>
    private static void CollectActions(
        ProcessStep group,
        Dictionary<Guid, List<ProcessStep>> childrenByParent,
        JsonArray actions,
        List<ImportDeploymentProcessWarning> warnings,
        ProcessStep topGroup)
    {
        foreach (var child in childrenByParent.GetValueOrDefault(group.Id) ?? [])
        {
            if (child.StepType == KrakenStepTypes.StepGroup)
            {
                warnings.Add(new(child.Name,
                    "Nested step group flattened — Octopus's shape has a single step→actions level."));
                CollectActions(child, childrenByParent, actions, warnings, topGroup);
                continue;
            }

            // The importer applies the STEP-level condition to every child
            // when present; a child whose own condition differs from the
            // group's will re-import with the group's. Surface that honestly.
            if (topGroup.Condition != StepCondition.Success && child.Condition != topGroup.Condition)
            {
                warnings.Add(new(child.Name,
                    $"Child run-condition '{child.Condition}' differs from group '{topGroup.Condition}'; " +
                    "the group's condition wins on re-import."));
            }
            actions.Add(ActionObj(child, warnings));
        }
    }

    /// <summary>
    /// Step-level shell: Name + Condition + StartTrigger + Properties (roles,
    /// variable expression). For groups the default Success condition is
    /// OMITTED so each child action's own condition survives re-import (the
    /// importer prefers the step-level value when present).
    /// </summary>
    private static JsonObject StepShell(ProcessStep step, bool omitConditionWhenDefault)
    {
        var props = new JsonObject();
        if (step.TargetRoles.Count > 0)
        {
            props["Octopus.Action.TargetRoles"] = string.Join(",", step.TargetRoles);
        }
        if (!string.IsNullOrWhiteSpace(step.ConditionVariableExpression))
        {
            props["Octopus.Step.ConditionVariableExpression"] = step.ConditionVariableExpression;
        }

        var obj = new JsonObject
        {
            ["Name"] = step.Name,
            ["StartTrigger"] = step.StartTrigger.ToString(),
            ["Properties"] = props,
        };
        if (!omitConditionWhenDefault || step.Condition != StepCondition.Success)
        {
            obj["Condition"] = step.Condition.ToString();
        }
        return obj;
    }

    private static JsonObject ActionObj(ProcessStep step, List<ImportDeploymentProcessWarning> warnings)
    {
        // Config verbatim — values were normalised to strings on import (or
        // authored as strings in the editor), so emitting them as strings is
        // the exact inverse of NormalisePropertyValue for the common case.
        var props = new JsonObject();
        foreach (var (key, value) in step.Config)
        {
            props[key] = value;
        }

        // Typed knobs the importer reads back from the property bag.
        if (step.MaxRetries > 0 && !step.Config.ContainsKey("Octopus.Action.AutoRetry.MaximumCount"))
        {
            props["Octopus.Action.AutoRetry.MaximumCount"] =
                step.MaxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // D3 — re-emit RunOnServer from its typed column (leaf/script flag),
        // emit-only-when-true so a round-trip stays minimal.
        if (step.RunOnServer && !step.Config.ContainsKey(KrakenScriptConfigKeys.RunOnServer))
        {
            props[KrakenScriptConfigKeys.RunOnServer] = "true";
        }

        // Knobs with no Octopus equivalent → Kraken extension keys. They
        // round-trip into Config (visible/editable), not the typed fields.
        if (step.RetryDelaySeconds > 0 && !step.Config.ContainsKey("Kraken.Action.RetryDelaySeconds"))
        {
            props["Kraken.Action.RetryDelaySeconds"] =
                step.RetryDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            warnings.Add(new(step.Name,
                "RetryDelaySeconds exported as Config key 'Kraken.Action.RetryDelaySeconds' — re-import keeps it in Config, not the typed field."));
        }
        if (step.TimeoutSeconds > 0 && !step.Config.ContainsKey("Kraken.Action.TimeoutSeconds"))
        {
            props["Kraken.Action.TimeoutSeconds"] =
                step.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            warnings.Add(new(step.Name,
                "TimeoutSeconds exported as Config key 'Kraken.Action.TimeoutSeconds' — re-import keeps it in Config, not the typed field."));
        }

        var action = new JsonObject
        {
            ["Name"] = step.Name,
            ["ActionType"] = step.StepType,
            ["IsDisabled"] = false,
            ["IsRequired"] = step.Required,
            ["Condition"] = step.Condition.ToString(),
            ["Properties"] = props,
        };

        if (!string.IsNullOrWhiteSpace(step.PackageId))
        {
            action["Packages"] = new JsonArray(new JsonObject { ["PackageId"] = step.PackageId });
        }

        return action;
    }
}

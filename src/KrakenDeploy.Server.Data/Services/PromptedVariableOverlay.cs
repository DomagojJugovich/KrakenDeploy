using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Data.Services;

public static class PromptedVariableOverlay
{
    public static IReadOnlyCollection<string> Apply(
        StepScopedResolution resolution,
        IReadOnlyList<VariableSnapshot> snapshot,
        PromptedVariableContext context,
        IReadOnlyList<Guid> stepIds,
        IReadOnlyDictionary<string, string> promptedValues)
    {
        var sensitiveNames = resolution.SensitiveNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in promptedValues)
        {
            var deploymentWinner = PromptedVariableResolver.FindWinner(snapshot, name, context);
            if (deploymentWinner?.IsPrompted == true)
            {
                resolution.DeploymentWide[deploymentWinner.Name] = value;
                AddSensitivity(deploymentWinner, sensitiveNames);
            }

            foreach (var stepId in stepIds)
            {
                var stepWinner = PromptedVariableResolver.FindWinner(snapshot, name, context, stepId);
                if (stepWinner?.IsPrompted != true)
                {
                    continue;
                }
                if (!resolution.PerStepDelta.TryGetValue(stepId, out var delta))
                {
                    delta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    resolution.PerStepDelta[stepId] = delta;
                }
                delta[stepWinner.Name] = value;
                AddSensitivity(stepWinner, sensitiveNames);
            }
        }
        return sensitiveNames;
    }

    private static void AddSensitivity(
        VariableSnapshot variable,
        HashSet<string> sensitiveNames)
    {
        if (variable.Type == VariableType.Sensitive)
        {
            sensitiveNames.Add(variable.Name);
        }
    }
}

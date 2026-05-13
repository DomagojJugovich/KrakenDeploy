using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// A single output variable captured during a deployment step via an
/// Octopus-compatible <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c>
/// stdout marker. Persisted per deployment + step + name so the deployment
/// detail page can surface "step X produced these outputs" and so subsequent
/// audit can reconstruct exactly what each step emitted.
/// </summary>
public class DeploymentOutputVariable : Entity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Name of the step that emitted the variable (matches <c>DeploymentStep.Name</c>).</summary>
    public required string StepName { get; set; }

    /// <summary>Variable name as supplied to <c>Set-OctopusVariable -name</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Variable value (may be multi-line; stored as-is after base64 decode on the agent).</summary>
    public string Value { get; set; } = "";

    /// <summary>When the variable was captured.</summary>
    public DateTimeOffset CapturedUtc { get; set; }
}

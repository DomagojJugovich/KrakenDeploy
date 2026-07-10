using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// A single output variable captured during a task step via an Octopus-compatible
/// <c>Set-OctopusVariable</c> / <c>##octopus[setVariable]</c> stdout marker
/// (formerly <c>DeploymentOutputVariable</c>). Persisted per task + step + name;
/// now written for runbook runs too (the pre-unification AgentHub drop is fixed).
/// </summary>
public class TaskOutputVariable : Entity, ISpaceScoped
{
    /// <summary>Inherited from the parent task; set explicitly at the write site
    /// (agent/transport path has no real Space context).</summary>
    public Guid SpaceId { get; set; }

    public Guid TaskId { get; set; }
    public ServerTask Task { get; set; } = null!;

    /// <summary>Name of the step that emitted the variable.</summary>
    public required string StepName { get; set; }

    /// <summary>Variable name as supplied to <c>Set-OctopusVariable -name</c>.</summary>
    public required string Name { get; set; }

    /// <summary>Variable value (may be multi-line; stored as-is after base64 decode on the agent).</summary>
    public string Value { get; set; } = "";

    /// <summary>When the variable was captured.</summary>
    public DateTimeOffset CapturedUtc { get; set; }
}

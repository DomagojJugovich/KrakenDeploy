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

    /// <summary>
    /// Variable value. For non-sensitive outputs this is the plaintext value
    /// (base64-decoded on the agent). For sensitive outputs
    /// (<see cref="IsSensitive"/> = <c>true</c>) it is the AES-GCM ciphertext
    /// produced by <c>IEncryptionService.Encrypt</c> under the active DEK (T0-6)
    /// — never expose it directly; the read path masks it to <c>***</c>.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// T0-6: the value was emitted with <c>Set-OctopusVariable -sensitive</c>.
    /// When set, <see cref="Value"/> holds ciphertext and the UI masks it. The
    /// DEK-rotation walk re-encrypts only these rows.
    /// </summary>
    public bool IsSensitive { get; set; }

    /// <summary>When the variable was captured.</summary>
    public DateTimeOffset CapturedUtc { get; set; }
}

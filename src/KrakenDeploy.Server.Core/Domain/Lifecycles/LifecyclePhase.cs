namespace KrakenDeploy.Server.Core.Domain.Lifecycles;

/// <summary>
/// One ordered phase in a <see cref="Lifecycle"/>. Stored as a JSONB array inside the
/// <c>lifecycles</c> row — not a separate table.
/// <para>
/// Gate rule: a deployment to a later phase's environment is only allowed once all
/// <em>required</em> environments in all earlier non-optional phases have been
/// successfully deployed for the same release (and tenant, if present).
/// </para>
/// </summary>
public class LifecyclePhase
{
    /// <summary>Stable identifier within the lifecycle (used for retention scoping).</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    /// <summary>Zero-based ordering within the lifecycle.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Required environments. All (or <see cref="MinimumEnvironments"/>) must have a successful
    /// deployment before the next phase can be entered.
    /// </summary>
    public List<Guid> EnvironmentIds { get; set; } = [];

    /// <summary>Optional environments — deployable but not required to advance.</summary>
    public List<Guid> OptionalEnvironmentIds { get; set; } = [];

    /// <summary>
    /// Minimum number of required environments that must succeed to complete the phase.
    /// <c>0</c> means all environments in <see cref="EnvironmentIds"/> must succeed.
    /// </summary>
    public int MinimumEnvironments { get; set; }

    /// <summary>
    /// When <c>true</c>, this phase may be skipped entirely without blocking later phases.
    /// </summary>
    public bool IsOptional { get; set; }

    /// <summary>
    /// Maximum number of successful deployments to retain per environment in this phase.
    /// <c>0</c> means keep all (no pruning).
    /// </summary>
    public int RetentionKeepDeployments { get; set; }
}

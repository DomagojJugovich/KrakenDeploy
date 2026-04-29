namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Scope constraints that narrow which deployments a variable applies to.
/// Stored as <c>jsonb</c> inside the <c>variables</c> table.
/// <para>
/// A <c>null</c> constraint means "any" — e.g., a variable with no
/// <see cref="EnvironmentId"/> applies to all environments.
/// An unscoped variable (all constraints null) is the universal fallback.
/// </para>
/// <para>
/// Scope resolution priority (higher score wins for the same variable name):
/// <list type="table">
///   <item><term>+4</term><description>EnvironmentId is set and matches</description></item>
///   <item><term>+2</term><description>Roles overlap with the target's roles</description></item>
///   <item><term>+1</term><description>TargetId is set and matches</description></item>
/// </list>
/// The highest-scoring matching variable for each name wins.
/// </para>
/// </summary>
public class VariableScope
{
    /// <summary>If set, the variable applies only to this environment.</summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>If set, the variable applies only to this specific target machine.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>
    /// If non-empty, the variable applies only to targets whose roles
    /// overlap with at least one entry in this list.
    /// </summary>
    public List<string>? Roles { get; set; }

    // ── Helpers (not persisted) ──────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when no constraints are set — the variable acts
    /// as a project-wide default.
    /// </summary>
    public bool IsUnscoped =>
        EnvironmentId is null &&
        TargetId is null &&
        (Roles is null || Roles.Count == 0);

    /// <summary>
    /// Specificity score used to break ties when multiple variables with the
    /// same name match a deployment context.
    /// </summary>
    public int SpecificityScore()
    {
        var score = 0;

        if (EnvironmentId.HasValue)
        {
            score += 4;
        }

        if (Roles is { Count: > 0 })
        {
            score += 2;
        }

        if (TargetId.HasValue)
        {
            score += 1;
        }

        return score;
    }

    /// <summary>
    /// Returns <c>true</c> when this scope matches the given deployment context.
    /// Each non-null constraint must be satisfied.
    /// </summary>
    public bool Matches(
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles)
    {
        if (EnvironmentId.HasValue && EnvironmentId.Value != environmentId)
        {
            return false;
        }

        if (TargetId.HasValue && (!targetId.HasValue || TargetId.Value != targetId.Value))
        {
            return false;
        }

        if (Roles is { Count: > 0 } &&
            !Roles.Intersect(targetRoles, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        return true;
    }
}

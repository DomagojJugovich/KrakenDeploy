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
/// Scope resolution is Octopus-compatible: the most-specific matching scope
/// wins for each variable name. Specificity is a <b>place-value rank</b> (a
/// more-specific dimension always beats any combination of less-specific ones),
/// following Octopus's ordered scope list. For the dimensions KrakenDeploy
/// models today the order, most specific first, is: <b>step &gt; target (machine) &gt;
/// roles (target tags) &gt; tenant &gt; environment &gt; channel</b>. When two definitions
/// are scoped <i>equally</i>, the source breaks the tie (project &gt; library
/// &gt; tenant) — handled by the resolver, not this scope object.
/// </para>
/// </summary>
public class VariableScope
{
    /// <summary>If set, the variable applies only when deploying for this tenant.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>If set, the variable applies only to this environment.</summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>If set, the variable applies only to this specific target machine.</summary>
    public Guid? TargetId { get; set; }

    /// <summary>
    /// If non-empty, the variable applies only to targets whose roles
    /// overlap with at least one entry in this list.
    /// </summary>
    public List<string>? Roles { get; set; }

    /// <summary>
    /// If set, the variable applies only when the deployment's release belongs
    /// to this channel. Channels are project-specific, so this only makes sense
    /// on project variables (not library / tenant sets). Per Octopus's ordering
    /// a channel scope is LESS specific than an environment scope.
    /// </summary>
    public Guid? ChannelId { get; set; }

    /// <summary>
    /// If set, the variable applies only to the deployment step with this ID
    /// (matched against the snapshot step's <c>Id</c> at deploy time). The
    /// MOST specific scope dimension. Steps are project-specific, so this only
    /// makes sense on project variables — never library / tenant sets.
    /// Stable across step renames; orphaned on step deletion.
    /// </summary>
    public Guid? ProcessStepId { get; set; }

    // ── Helpers (not persisted) ──────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when no constraints are set — the variable acts
    /// as a project-wide default.
    /// </summary>
    public bool IsUnscoped =>
        TenantId is null &&
        EnvironmentId is null &&
        TargetId is null &&
        ChannelId is null &&
        ProcessStepId is null &&
        (Roles is null || Roles.Count == 0);

    /// <summary>
    /// Scope-specificity rank — higher wins. A PLACE-VALUE rank (bitmask), not a
    /// sum: a more-specific dimension always outranks any combination of
    /// less-specific ones, matching Octopus's ordered scope-specificity list
    /// (most specific first): step/action, machine (target), target-tags-by-step,
    /// target-tags (roles), tenant, tenant-tag, environment, channel, process.
    /// KrakenDeploy currently models target, roles, tenant and environment; the
    /// other slots are reserved so the order stays correct once they're added.
    /// </summary>
    public int SpecificityScore()
    {
        var rank = 0;
        if (ProcessStepId.HasValue) { rank |= 1 << 9; } // step / action (most specific)
        if (TargetId.HasValue)       { rank |= 1 << 8; } // machine / deployment target
        if (Roles is { Count: > 0 }) { rank |= 1 << 6; } // target tags / roles
        if (TenantId.HasValue)       { rank |= 1 << 5; } // target tenant
        if (EnvironmentId.HasValue)  { rank |= 1 << 3; } // environment
        if (ChannelId.HasValue)      { rank |= 1 << 2; } // channel (less specific than environment)
        return rank;
    }

    /// <summary>
    /// Returns <c>true</c> when this scope matches the given deployment context.
    /// Each non-null constraint must be satisfied.
    /// </summary>
    public bool Matches(
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId = null,
        Guid? channelId = null,
        Guid? stepId = null)
    {
        if (TenantId.HasValue && TenantId.Value != tenantId)
        {
            return false;
        }

        if (EnvironmentId.HasValue && EnvironmentId.Value != environmentId)
        {
            return false;
        }

        if (ChannelId.HasValue && ChannelId.Value != channelId)
        {
            return false;
        }

        if (ProcessStepId.HasValue && ProcessStepId.Value != stepId)
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

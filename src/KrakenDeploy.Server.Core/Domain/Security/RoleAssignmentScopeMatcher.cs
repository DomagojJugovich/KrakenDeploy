namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Pure scope-matching predicate for <see cref="RoleAssignment"/> ↔
/// <see cref="PermissionScope"/>. Centralises the AND-between-dimensions /
/// OR-within-dimension semantics so both <c>PermissionEvaluator</c> and any
/// future query-side filtering use the same rules.
/// <para>
/// Lives in <c>Server.Core</c> rather than the data layer specifically so
/// it can be unit-tested without spinning up a DbContext.
/// </para>
/// </summary>
public static class RoleAssignmentScopeMatcher
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="assignment"/> applies to the
    /// requested <paramref name="scope"/>.
    /// <para>
    /// Semantics, per dimension (Project / Environment / Tenant / etc.):
    /// </para>
    /// <list type="bullet">
    ///   <item>If the assignment's id list is empty, that dimension is
    ///         unrestricted and always matches.</item>
    ///   <item>If the assignment's id list is non-empty AND the scope pins a
    ///         specific id, the id must be in the list.</item>
    ///   <item>If the assignment's id list is non-empty AND the scope leaves
    ///         the dimension <c>null</c> (caller didn't restrict to a specific
    ///         entity), the assignment <em>still matches</em>. Rationale: the
    ///         caller is asking "what could I do somewhere?", and the
    ///         assignment grants something somewhere — the UI uses this for
    ///         "do I see this menu / button at all?" decisions. For per-entity
    ///         "can I act here?" checks, the caller passes the specific id and
    ///         the restrictive branch above kicks in.</item>
    /// </list>
    /// <para>
    /// Dimensions are AND'd: every dimension must match. Empty assignment-list
    /// dimensions auto-pass; mismatched specific ids cause the whole
    /// assignment to be excluded.
    /// </para>
    /// <para>
    /// SpaceId matching is the caller's responsibility — the
    /// <c>PermissionEvaluator</c> already filters assignments by SpaceId before
    /// calling this method.
    /// </para>
    /// <para>
    /// <strong>Strict mode (T1-8):</strong> when <paramref name="strict"/> is
    /// <c>true</c> the optimistic-null branch is disabled — a dimension the
    /// assignment RESTRICTS but the caller left <c>null</c> is treated as a
    /// MISMATCH, not a pass. This is the authoritative mode for WRITE/execute
    /// checks: the caller must supply the concrete Project/Environment/Tenant,
    /// so a grant scoped to (say) Environment=Test can never leak into a
    /// deployment to Prod just because the check under-specified the scope. A
    /// genuinely unrestricted dimension (empty id list) still auto-passes — a
    /// Space-wide grant legitimately covers every entity. Broad READ checks keep
    /// the default optimistic behaviour (<paramref name="strict"/> = false).
    /// </para>
    /// </summary>
    public static bool Matches(RoleAssignment assignment, PermissionScope scope, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // No Tag dimension: tag_ids was dormant and is dropped (fix 7). Each
        // list is a projection over the assignment's scope rows.
        return DimensionMatches(assignment.ProjectGroupIds, scope.ProjectGroupId, strict)
            && DimensionMatches(assignment.ProjectIds,      scope.ProjectId,      strict)
            && DimensionMatches(assignment.EnvironmentIds,  scope.EnvironmentId,  strict)
            && DimensionMatches(assignment.TenantIds,       scope.TenantId,       strict);
    }

    private static bool DimensionMatches(IReadOnlyList<Guid> assignmentIds, Guid? scopeId, bool strict)
    {
        // No rows for the assignment in this dimension = "all" → matches every
        // scope value (including null). Holds in strict mode too: a Space-wide
        // grant legitimately covers everything.
        if (assignmentIds.Count == 0)
        {
            return true;
        }

        // Caller didn't pin this dimension.
        //  - optimistic (reads): match — "could I act somewhere?"
        //  - strict (writes): the assignment is restricted here and the caller
        //    failed to supply the concrete id → fail closed.
        if (scopeId is null)
        {
            return !strict;
        }

        // Both restricted — actual membership check.
        return assignmentIds.Contains(scopeId.Value);
    }
}

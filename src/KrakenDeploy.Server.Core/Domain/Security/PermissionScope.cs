namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// The set of dimensions a permission check can be evaluated against. Used
/// by <see cref="IPermissionEvaluator"/>: a check matches a
/// <see cref="RoleAssignment"/> if every non-null dimension on the scope is
/// either present in the assignment's matching list or the assignment's list
/// is empty (= "all" for that dimension).
/// <para>
/// Setting all dimensions to <c>null</c> evaluates against the assignment's
/// Space-wide grant only — useful for "can I see this Space at all?" checks.
/// </para>
/// </summary>
public readonly record struct PermissionScope(
    Guid? SpaceId         = null,
    Guid? ProjectGroupId  = null,
    Guid? ProjectId       = null,
    Guid? EnvironmentId   = null,
    Guid? TenantId        = null,
    Guid? TagId           = null)
{
    /// <summary>True when no dimension is specified — a system-wide check.</summary>
    public bool IsSystemWide =>
        SpaceId is null &&
        ProjectGroupId is null &&
        ProjectId is null &&
        EnvironmentId is null &&
        TenantId is null &&
        TagId is null;
}

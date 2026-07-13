namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// One scope-restriction row on a <see cref="RoleAssignment"/>: a single
/// (dimension, id) pair narrowing the grant.
/// <para>
/// Physical shape uses one nullable FK column per dimension (an exclusive arc:
/// a CHECK enforces exactly one is non-null) rather than a single polymorphic
/// <c>ref_id</c>. This is deliberate — Postgres cannot attach a per-dimension
/// FK to a shared column, and per-dimension <c>ON DELETE CASCADE</c> (a deleted
/// project / environment / tenant / group vanishing from every grant) is the
/// entire reason this table replaces the old jsonb Guid arrays. There is no
/// Tag dimension: <c>tag_ids</c> was dormant and is dropped.
/// </para>
/// </summary>
public class RoleAssignmentScope
{
    public Guid Id { get; set; }

    public Guid RoleAssignmentId { get; set; }
    public RoleAssignment RoleAssignment { get; set; } = null!;

    /// <summary>Set iff this row scopes the grant to a project group.</summary>
    public Guid? ProjectGroupId { get; set; }

    /// <summary>Set iff this row scopes the grant to a project.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Set iff this row scopes the grant to an environment.</summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>Set iff this row scopes the grant to a tenant.</summary>
    public Guid? TenantId { get; set; }
}

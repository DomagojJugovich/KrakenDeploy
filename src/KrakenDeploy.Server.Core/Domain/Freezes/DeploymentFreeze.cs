using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Freezes;

/// <summary>
/// A time window during which deployments matching the configured scope
/// are blocked (M13.F.2). Octopus parity: freezes are <em>global</em>
/// across multiple projects — release weeks, holidays, weekend lockdowns.
///
/// <para>
/// Scope semantics: the freeze applies when ALL configured scope fields
/// match the deployment under consideration. An empty scope list = "any"
/// for that dimension. So "all projects in this Space + only Production
/// environment" → set <c>ProjectIds</c> = empty + <c>EnvironmentIds</c> =
/// {production-id}.
/// </para>
///
/// <para>
/// Override is permission-gated (<c>DeploymentFreezeOverride</c>), not
/// freeze-row-attribute-gated. A freeze without the ability to override
/// would block emergency security-patch hotfixes — every freeze permits
/// override by privileged roles, the audit log captures every use.
/// </para>
/// </summary>
public class DeploymentFreeze : AuditableEntity, ISpaceScoped
{
    /// <summary>Owning Space — freezes are Space-scoped (each Space has
    /// its own freeze policies). The scope sub-fields then narrow further
    /// inside this Space.</summary>
    public Guid SpaceId { get; set; }

    /// <summary>Operator-facing label, e.g. "Q4 release freeze",
    /// "Christmas lockdown". Appears verbatim in the deployment-blocked
    /// error message so make it self-explanatory.</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional longer rationale. Surfaced as a tooltip on the
    /// freezes page; not shown in the blocked-deployment error (which
    /// uses <see cref="Name"/> only).</summary>
    public string? Description { get; set; }

    /// <summary>Window start, inclusive. Always UTC.</summary>
    public DateTimeOffset StartUtc { get; set; }

    /// <summary>Window end, exclusive. Always UTC. Must be after
    /// <see cref="StartUtc"/>; enforced at the service layer.</summary>
    public DateTimeOffset EndUtc { get; set; }

    /// <summary>
    /// Project filter. Empty = applies to every project in the Space.
    /// Non-empty = applies only when the deployment's project is in the list.
    /// Stored as JSONB column.
    /// </summary>
    public List<Guid> ProjectIds { get; set; } = [];

    /// <summary>
    /// Environment filter. Empty = applies to every environment.
    /// Non-empty = applies only when the deployment's environment is in
    /// the list. Stored as JSONB column.
    /// </summary>
    public List<Guid> EnvironmentIds { get; set; } = [];

    // The tag-filter dimension (tag_ids) was dropped in the 2026-07 cleanup: it
    // was dormant end-to-end (no UI tag picker; the dispatch gate always passed
    // null), and fix 4's role-assignment scope table deliberately has no tag
    // dimension. Freeze-by-tag can be reintroduced as its own feature if needed.

    /// <summary>
    /// True = soft-disabled (kept on the page but not enforced). The
    /// "draft this lockdown for next month" workflow.
    /// </summary>
    public bool Disabled { get; set; }

    // ── Convenience ────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="now"/> falls inside the window
    /// and the freeze is enabled.</summary>
    public bool IsActiveAt(DateTimeOffset now)
        => !Disabled && now >= StartUtc && now < EndUtc;
}

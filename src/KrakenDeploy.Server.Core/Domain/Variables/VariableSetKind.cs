namespace KrakenDeploy.Server.Core.Domain.Variables;

/// <summary>
/// Discriminates the role a <see cref="VariableSet"/> plays.
/// </summary>
public enum VariableSetKind
{
    /// <summary>
    /// One-to-one with a <see cref="Projects.Project"/> — the project's own
    /// variables. Carries a non-null <c>ProjectId</c> and no <c>Name</c>.
    /// </summary>
    Project = 0,

    /// <summary>
    /// A reusable library variable set that projects can include. Carries a
    /// <c>Name</c> and no <c>ProjectId</c>; surfaced on the global
    /// <c>/variable-sets</c> page and included per-project.
    /// </summary>
    Library = 1,

    /// <summary>
    /// Tenant common variables (referenced by <c>Tenant.VariableSetId</c>).
    /// Reserved — the live resolver already overlays it, but no creation path
    /// is wired yet.
    /// </summary>
    TenantCommon = 2,
}

namespace KrakenDeploy.Server.Core.Domain.Licensing;

/// <summary>
/// Abstraction that lets data-layer write paths consult the active license
/// before creating quota-bearing rows (deployment targets, user accounts, ...).
/// Concrete implementation lives in <c>KrakenDeploy.Server</c> and wraps
/// <c>LicenseService</c>; the interface keeps <c>Server.Data</c> free of the
/// JWT / RSA dependency chain.
///
/// The contract is intentionally a <em>pre-flight</em> check — callers count
/// existing rows (across <em>all</em> Spaces, via <c>IgnoreQueryFilters</c>;
/// license limits are server-wide, not per-Space) and ask whether one more is
/// allowed. The gate returns <see langword="null"/> on success or a user-facing
/// reason string on refusal.
/// </summary>
public interface ILicenseGate
{
    /// <summary>
    /// Returns <see langword="null"/> when adding one more deployment target is
    /// allowed under the current license. Returns a user-facing refusal message
    /// (e.g. "Target limit reached (10/10). Upgrade your license to add more
    /// targets.") when the cap is hit.
    /// </summary>
    /// <param name="currentTargetCount">
    /// Number of <c>DeploymentTarget</c> rows that currently exist across all
    /// Spaces. The caller MUST count under <c>IgnoreQueryFilters</c> — a
    /// per-Space count would let an operator exceed the server-wide cap by
    /// rotating Spaces.
    /// </param>
    string? CheckTargetCreate(int currentTargetCount);

    /// <summary>
    /// Returns <see langword="null"/> when adding one more user is allowed
    /// under the current license. Returns a user-facing refusal message when
    /// the cap is hit. Counts all <c>ApplicationUser</c> rows.
    /// </summary>
    string? CheckUserCreate(int currentUserCount);
}

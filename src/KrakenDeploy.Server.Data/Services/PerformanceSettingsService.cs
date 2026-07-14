using KrakenDeploy.Server.Core.Domain.Performance;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read + write surface for the instance-wide performance / retention knobs
/// (M13.F.3). Thin wrapper over the System-scoped <c>performance</c> settings
/// document (via <see cref="SettingsService"/>, which owns the cache).
///
/// <para>
/// Read sites: <c>Program.cs</c> (Hangfire worker count at startup),
/// <c>AuditRetentionJob</c> / <c>AiCallLogRetentionJob</c> (retention windows),
/// and <c>DeploymentWorker</c> (slow-deployment thresholds). Retention precedence
/// (DB row wins, then <c>appsettings.json</c>, then defaults) is enforced by the
/// jobs via <see cref="SettingsService.TryGetAsync{T}"/> to distinguish "operator
/// never saved" from "operator saved defaults".
/// </para>
/// </summary>
public sealed class PerformanceSettingsService(SettingsService settings)
{
    /// <summary>
    /// Returns the current settings, or a transient defaults instance when the
    /// operator has never saved the page (property initializers carry the
    /// hardcoded defaults).
    /// </summary>
    public Task<PerformanceSettings> GetAsync(CancellationToken ct = default)
        => settings.GetAsync<PerformanceSettings>(ct: ct);

    /// <summary>
    /// Persists the supplied settings. Caller is responsible for input validation
    /// (the page's edit form enforces non-negative ints + a sensible upper bound).
    /// </summary>
    public async Task SaveAsync(PerformanceSettings update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await settings.SaveAsync(update, ct: ct).ConfigureAwait(false);
    }

    public void InvalidateCache() => settings.Invalidate<PerformanceSettings>();
}

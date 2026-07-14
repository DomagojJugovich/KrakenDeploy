using KrakenDeploy.Server.Core.Domain.Maintenance;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read + write surface for the instance-wide maintenance flag. Thin wrapper over
/// the System-scoped <c>maintenance</c> settings document (via
/// <see cref="SettingsService"/>, which owns the short-TTL cache the per-request
/// maintenance middleware relies on).
/// </summary>
public sealed class MaintenanceModeService(SettingsService settings, TimeProvider time)
{
    /// <summary>Current maintenance state. Returns "off" when never configured.</summary>
    public async Task<MaintenanceState> GetStateAsync(CancellationToken ct = default)
    {
        var doc = await settings.GetAsync<MaintenanceSettings>(ct: ct).ConfigureAwait(false);
        return doc.Enabled
            ? new MaintenanceState(true, doc.Reason, doc.EnabledByUserId, doc.EnabledUtc)
            : MaintenanceState.Off;
    }

    public Task EnableAsync(string? reason, Guid? userId, CancellationToken ct = default)
        => settings.MutateAsync<MaintenanceSettings>(scopeId: null, m =>
        {
            m.Enabled         = true;
            m.Reason          = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            m.EnabledByUserId = userId;
            m.EnabledUtc      = time.GetUtcNow();
            return m;
        }, ct);

    public Task DisableAsync(CancellationToken ct = default)
        => settings.MutateAsync<MaintenanceSettings>(scopeId: null, m =>
        {
            m.Enabled         = false;
            m.Reason          = null;
            m.EnabledByUserId = null;
            m.EnabledUtc      = null;
            return m;
        }, ct);

    public void InvalidateCache() => settings.Invalidate<MaintenanceSettings>();
}

/// <summary>Immutable snapshot of the maintenance flag — what the
/// middleware and the page bind against.</summary>
public sealed record MaintenanceState(
    bool Enabled,
    string? Reason,
    Guid? EnabledByUserId,
    DateTimeOffset? EnabledUtc)
{
    public static readonly MaintenanceState Off = new(false, null, null, null);
}

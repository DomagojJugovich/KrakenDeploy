using KrakenDeploy.Server.Core.Domain.Features;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Read/write surface for per-instance feature toggles (M13.F.1). Backed by the
/// single System-scoped <c>features</c> settings document (via
/// <see cref="SettingsService"/>, which owns the short-TTL cache). Toggles are
/// stored as a map of overrides keyed by feature key; a key at its catalogue
/// default has no entry (toggling back to default removes it).
/// </summary>
public sealed class FeatureFlagService(SettingsService settings, IFeatureCatalog catalog)
{
    /// <summary>
    /// Returns the effective state for <paramref name="key"/> — override when
    /// present, catalogue default otherwise. Unknown keys throw so typos at call
    /// sites fail loudly instead of silently returning false.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var descriptor = catalog.Find(key)
            ?? throw new InvalidOperationException(
                $"Feature '{key}' is not registered in IFeatureCatalog. " +
                "Check the spelling or add an entry to BuiltInFeatureCatalog.");

        var doc = await settings.GetAsync<FeatureFlagsDocument>(ct: ct).ConfigureAwait(false);
        return doc.Overrides.TryGetValue(key, out var explicitState)
            ? explicitState
            : descriptor.DefaultEnabled;
    }

    /// <summary>
    /// Returns one <see cref="FeatureState"/> per catalogue entry, with the
    /// effective enabled state + a flag indicating whether the value comes from an
    /// override or the catalogue default (drives the "(default)" hint).
    /// </summary>
    public async Task<List<FeatureState>> GetAllAsync(CancellationToken ct = default)
    {
        var doc = await settings.GetAsync<FeatureFlagsDocument>(ct: ct).ConfigureAwait(false);
        return [.. catalog.All.Select(d =>
            new FeatureState(
                Descriptor: d,
                Enabled:    doc.Overrides.TryGetValue(d.Key, out var v) ? v : d.DefaultEnabled,
                IsOverride: doc.Overrides.ContainsKey(d.Key)))];
    }

    /// <summary>
    /// Persists an explicit override. When the requested state matches the
    /// catalogue default, removes the override entry instead — keeps the document
    /// to genuinely-changed flags. Concurrency-safe: the read-modify-write on the
    /// single overrides document is retried on an xmin conflict, so two
    /// concurrent toggles of different keys never clobber each other.
    /// </summary>
    public async Task SetAsync(string key, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var descriptor = catalog.Find(key)
            ?? throw new InvalidOperationException(
                $"Feature '{key}' is not registered in IFeatureCatalog.");

        await settings.MutateAsync<FeatureFlagsDocument>(scopeId: null, doc =>
        {
            if (enabled == descriptor.DefaultEnabled)
            {
                doc.Overrides.Remove(key);
            }
            else
            {
                doc.Overrides[key] = enabled;
            }
            return doc;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Drop the cache. Mostly used by tests.</summary>
    public void Invalidate() => settings.Invalidate<FeatureFlagsDocument>();
}

/// <summary>One row in the page's grid — effective state + provenance.</summary>
public sealed record FeatureState(
    FeatureDescriptor Descriptor,
    bool Enabled,
    bool IsOverride);

using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.Features;

/// <summary>
/// System-scoped <see cref="ISettingsDocument"/> (key <c>"features"</c>) holding
/// the per-instance feature-toggle overrides as a single document — the fold of
/// the former one-row-per-flag <c>feature_flags</c> table.
///
/// <para>
/// A key present in <see cref="Overrides"/> differs from its catalogue default
/// (<see cref="IFeatureCatalog"/> / <see cref="FeatureDescriptor.DefaultEnabled"/>).
/// A key that is absent takes the catalogue default — so adding a new feature in
/// code Just Works without a migration. Toggling a flag back to its default
/// removes the entry (the old "delete the override row" becomes "remove the map
/// entry"), keeping the document to only genuinely-changed flags.
/// </para>
/// <para>
/// Server-wide, NOT per-Space (per-Space AI feature flags are a separate
/// concern that lives on the AI settings document).
/// </para>
/// </summary>
public class FeatureFlagsDocument : ISettingsDocument
{
    /// <inheritdoc />
    public static string Key => "features";

    /// <inheritdoc />
    public static SettingsScope Scope => SettingsScope.System;

    /// <summary>
    /// Explicit overrides keyed by feature key. Present = the effective state
    /// differs from the catalogue default; absent = catalogue default applies.
    /// </summary>
    public Dictionary<string, bool> Overrides { get; set; } = [];
}

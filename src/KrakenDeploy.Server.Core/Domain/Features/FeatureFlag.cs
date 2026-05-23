using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Features;

/// <summary>
/// Persisted on/off state for a per-instance feature toggle (M13.F.1).
/// Server-wide — NOT per-Space (that's the AI feature flags' job, kept
/// separate on purpose; mixing the two surfaces would force operators
/// to remember which scope each toggle lives at).
///
/// <para>
/// Catalogue of available toggles lives in <see cref="IFeatureCatalog"/>.
/// Rows in this table are <em>overrides</em> — when no row exists for a
/// given key, the catalogue's <see cref="FeatureDescriptor.DefaultEnabled"/>
/// applies. That way, adding a new feature in code automatically Just
/// Works without a migration to seed the row.
/// </para>
/// </summary>
public class FeatureFlag : AuditableEntity
{
    /// <summary>Stable lookup key, dot-separated by topic, e.g.
    /// <c>feeds.step-template-catalog</c>, <c>onboarding.first-run-banner</c>.</summary>
    public string Key { get; set; } = "";

    /// <summary>Effective state for this toggle. Catalog default applies
    /// when no row exists.</summary>
    public bool Enabled { get; set; }
}

namespace KrakenDeploy.Server.Core.Domain.Features;

/// <summary>
/// Default <see cref="IFeatureCatalog"/> shipping with KrakenDeploy.
/// New per-instance feature toggles are added here as a single line; the
/// page picks them up automatically.
///
/// <para>
/// Adding a feature is intentionally lightweight (no DB migration, no
/// service-layer change) so it stays cheap to gate experimental work
/// behind an opt-in toggle. Removing one is a breaking change for any
/// override row pointing at the removed key — the row stays orphaned
/// until manually cleaned up; we treat that as benign (the service
/// returns the catalogue default for unknown keys).
/// </para>
/// </summary>
public sealed class BuiltInFeatureCatalog : IFeatureCatalog
{
    public IReadOnlyList<FeatureDescriptor> All { get; } =
    [
        // ── Feeds ──────────────────────────────────────────────────────
        new("feeds.step-template-catalog",
            FeatureGroups.Feeds,
            "Community step-template catalog (GitHub)",
            "Hourly poll of the OctopusDeploy/Library GitHub repo for community step " +
            "templates. Disable on air-gapped installs that must not call out to " +
            "GitHub. When off, the StepTemplateCatalogPollJob short-circuits without " +
            "making HTTP requests.",
            DefaultEnabled: true),

        new("feeds.step-package-catalog",
            FeatureGroups.Feeds,
            "Step-package catalog (GitHub releases)",
            "Hourly poll of the configured StepPackages catalogue repo (defaults to " +
            "KrakenDeploy/StepPackages) for new releases. Same air-gap caveat as the " +
            "step-template catalogue above.",
            DefaultEnabled: true),

        // ── Steps ──────────────────────────────────────────────────────
        new("steps.allow-unsigned-packages",
            FeatureGroups.Steps,
            "Allow unsigned step packages",
            "When enabled, the step-package installer accepts .kdeploy-step archives " +
            "without a valid manifest signature. Off-by-default in production; turn " +
            "on temporarily when bootstrapping a new signing key (D-12).",
            DefaultEnabled: false),

        // ── Onboarding ─────────────────────────────────────────────────
        new("onboarding.first-run-banner",
            FeatureGroups.Onboarding,
            "First-run setup banner",
            "Shows the 'next steps' banner on the dashboard while the instance has " +
            "no Projects + no Targets configured. Auto-hides once both lists are " +
            "non-empty; this toggle is for operators who prefer the banner stays " +
            "hidden even during initial setup.",
            DefaultEnabled: true),

        // ── Help ───────────────────────────────────────────────────────
        new("help.external-docs-links",
            FeatureGroups.Help,
            "External documentation links",
            "Shows 'Learn more' links pointing at the public docs site. Disable on " +
            "air-gapped installs where outbound links to docs.krakendeploy.com would " +
            "lead to a confusing dead-end browser tab.",
            DefaultEnabled: true),
    ];

    public FeatureDescriptor? Find(string key) =>
        All.FirstOrDefault(d => d.Key == key);
}

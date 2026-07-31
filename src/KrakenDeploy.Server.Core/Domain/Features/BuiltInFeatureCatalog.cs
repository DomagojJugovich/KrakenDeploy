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
        // ── Security ───────────────────────────────────────────────────
        new("security.allow-oidc-sign-in",
            FeatureGroups.Security,
            "Allow OIDC sign-in",
            "Master kill-switch for every configured Identity Provider. " +
            "When off, the login page hides the 'Sign in with X' buttons " +
            "and the /login/external challenge endpoint refuses with 503. " +
            "Local accounts continue to work. Useful during incident " +
            "response when an IdP misconfiguration is locking everyone " +
            "out — flip off, sign in with the bootstrap admin, fix the IdP, " +
            "flip back on.",
            DefaultEnabled: true),

        new("security.show-error-stack-traces",
            FeatureGroups.Security,
            "Show error stack traces in UI",
            "When on, the global Blazor error boundary and API exception " +
            "responses include the full stack trace. Off in production — " +
            "stack traces can leak internal paths, dependency versions, " +
            "and DB column names that aid attackers building a model of " +
            "the system. On in dev/staging where the trade-off flips.",
            DefaultEnabled: false),

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

        // ── Retention ──────────────────────────────────────────────────
        new("audit.purge-enabled",
            FeatureGroups.Retention,
            "Enable audit-log retention purge",
            "Master kill-switch for the nightly AuditRetentionJob. When off, " +
            "the job short-circuits regardless of the configured retention " +
            "window — operators can pause GDPR retention temporarily " +
            "(e.g. during a regulatory investigation that needs older rows " +
            "preserved) without losing the configured day count. Default ON.",
            DefaultEnabled: true),

        new("retention.sweep-dry-run",
            FeatureGroups.Retention,
            "Retention sweep dry-run mode",
            "When ON (the default), the scheduled retention sweep computes " +
            "exactly what it WOULD prune — packages, releases, runbook runs, " +
            "aged step logs, and the on-disk artifact / drop-bundle files " +
            "behind them — and writes that to the audit log WITHOUT deleting " +
            "anything. Flip OFF to let the sweep apply. Ships dry-run-first so " +
            "operators can verify the prune set on their real history before " +
            "any data is removed. The event-driven post-completion prune is " +
            "unaffected — it always applies.",
            DefaultEnabled: true),

        // ── UI ─────────────────────────────────────────────────────────
        new("ui.show-advanced-step-fields",
            FeatureGroups.Ui,
            "Show advanced step-editor fields",
            "Power-user fields in the step editor: raw JSON config, " +
            "Run-on-server toggle, Run Condition / Required / Retries / " +
            "Timeout (those land in M14). Off-by-default keeps the editor " +
            "approachable for typical operators; sysadmins flip on for " +
            "fine-grained control.",
            DefaultEnabled: false),

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

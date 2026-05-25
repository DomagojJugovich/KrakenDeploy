namespace KrakenDeploy.Server.Core.Domain.Features;

/// <summary>
/// Static catalogue of per-instance feature toggles known to the application.
/// Each entry carries the default state + UI labelling — the page renders
/// directly off this list, so adding a feature is a one-line code change.
///
/// <para>
/// Convention: keys are dot-separated and lowercase, e.g.
/// <c>feeds.step-template-catalog</c>. The first segment maps to the page
/// group ("Feeds", "Steps", "Onboarding", "Help" — case-insensitive
/// canonicalisation lives in <see cref="FeatureGroups"/>).
/// </para>
/// </summary>
public interface IFeatureCatalog
{
    /// <summary>All known features, in display order.</summary>
    IReadOnlyList<FeatureDescriptor> All { get; }

    /// <summary>Lookup by exact key. <see langword="null"/> when unknown.</summary>
    FeatureDescriptor? Find(string key);
}

/// <summary>One row in the catalogue.</summary>
/// <param name="Key">Stable lookup key.</param>
/// <param name="Group">Display group ("Feeds", "Steps", "Onboarding", "Help").</param>
/// <param name="Title">Short human-readable name shown next to the toggle.</param>
/// <param name="Description">Multi-line description shown below the toggle.</param>
/// <param name="DefaultEnabled">State when no override row exists.</param>
public sealed record FeatureDescriptor(
    string Key,
    string Group,
    string Title,
    string Description,
    bool DefaultEnabled);

/// <summary>Display-group constants. Pages iterate these to render
/// sections; new groups need a constant here so the page renders them
/// in a stable order.</summary>
public static class FeatureGroups
{
    public const string Security   = "Security";
    public const string Feeds      = "Feeds";
    public const string Steps      = "Steps";
    public const string Onboarding = "Onboarding";
    public const string Retention  = "Retention";
    public const string Ui         = "UI";
    public const string Help       = "Help";

    /// <summary>Display-order of groups on the features page. Security
    /// goes first — operators scanning the page during incident response
    /// want to find the OIDC kill-switch + stack-trace toggle without
    /// scrolling. Retention sits next to its dedicated card on the
    /// Performance page (cross-link in the UI).</summary>
    public static readonly IReadOnlyList<string> DisplayOrder =
    [
        Security,
        Feeds,
        Steps,
        Onboarding,
        Retention,
        Ui,
        Help,
    ];
}

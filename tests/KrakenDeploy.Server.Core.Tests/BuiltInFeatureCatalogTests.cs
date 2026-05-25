using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Features;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Unit tests pinning the BuiltInFeatureCatalog contract — keys + default
/// states + group membership. Catches a contributor accidentally
/// renaming a key (breaking saved overrides) or flipping a default state
/// that downstream code depends on.
/// </summary>
public sealed class BuiltInFeatureCatalogTests
{
    private readonly BuiltInFeatureCatalog _catalog = new();

    [Theory]
    // ── Pre-existing toggles (locked from M13.F.1) ───────────────────────
    [InlineData("feeds.step-template-catalog",      true,  FeatureGroups.Feeds)]
    [InlineData("feeds.step-package-catalog",       true,  FeatureGroups.Feeds)]
    [InlineData("steps.allow-unsigned-packages",    false, FeatureGroups.Steps)]
    [InlineData("onboarding.first-run-banner",      true,  FeatureGroups.Onboarding)]
    [InlineData("help.external-docs-links",         true,  FeatureGroups.Help)]
    // ── M13.F.5 additions ───────────────────────────────────────────────
    [InlineData("security.allow-oidc-sign-in",      true,  FeatureGroups.Security)]
    [InlineData("security.show-error-stack-traces", false, FeatureGroups.Security)]
    [InlineData("audit.purge-enabled",              true,  FeatureGroups.Retention)]
    [InlineData("ui.show-advanced-step-fields",     false, FeatureGroups.Ui)]
    public void Catalog_pins_key_group_and_default(
        string key, bool expectedDefault, string expectedGroup)
    {
        var descriptor = _catalog.Find(key);
        descriptor.Should().NotBeNull(
            "operators may have saved overrides keyed by '{0}'; renaming " +
            "the key orphans those rows + the page shows them as unknown",
            key);
        descriptor!.DefaultEnabled.Should().Be(expectedDefault);
        descriptor.Group.Should().Be(expectedGroup);
    }

    [Fact]
    public void All_descriptor_keys_are_unique()
    {
        // Saved overrides are looked up by exact key; a duplicate would
        // mean the second descriptor shadows the first silently.
        var keys = _catalog.All.Select(d => d.Key).ToList();
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void All_descriptor_groups_appear_in_DisplayOrder()
    {
        // The Features page iterates DisplayOrder to render sections;
        // a descriptor in an unlisted group would silently not render.
        var groupsInCatalog = _catalog.All.Select(d => d.Group).Distinct();
        groupsInCatalog.Should().BeSubsetOf(FeatureGroups.DisplayOrder);
    }

    [Fact]
    public void DisplayOrder_lists_Security_first()
    {
        // Operators scanning the page during incident response need to
        // find the OIDC kill-switch + stack-trace toggle without
        // scrolling. Pin the ordering.
        FeatureGroups.DisplayOrder[0].Should().Be(FeatureGroups.Security);
    }

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        _catalog.Find("nonexistent.feature").Should().BeNull();
    }
}

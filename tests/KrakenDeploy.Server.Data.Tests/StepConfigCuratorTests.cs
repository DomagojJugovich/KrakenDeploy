using FluentAssertions;
using KrakenDeploy.Server.Data.Services.Ai.Curators;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.B — unit tests for the step-config curators + the registry that
/// routes step types to them. Pure (no DB), so no Postgres collection.
/// Pins: per-type curation emits the expected slim keys, the script
/// curator truncates + hashes the body, the registry falls back to the
/// default for unknown types, and the default never leaks values.
/// </summary>
public sealed class StepConfigCuratorTests
{
    [Fact]
    public void Script_curator_truncates_body_and_emits_hash()
    {
        var curator = new ScriptStepConfigCurator();
        var longBody = new string('x', 500);
        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.Script.Syntax"]     = "PowerShell",
            ["Octopus.Action.PowerShell.Edition"] = "Core",
            ["Octopus.Action.Script.ScriptBody"] = longBody,
        };

        var summary = curator.Curate(config);

        summary["syntax"].Should().Be("PowerShell");
        summary["powerShellEdition"].Should().Be("Core");
        summary["scriptPreview"].Should().StartWith(new string('x', 200))
            .And.Contain("500 chars total");
        summary["scriptPreview"].Length.Should().BeLessThan(longBody.Length,
            because: "the preview is truncated to keep the LLM context lean");
        summary["scriptSha256"].Should().HaveLength(12);
    }

    [Fact]
    public void Script_curator_short_body_is_not_truncated()
    {
        var curator = new ScriptStepConfigCurator();
        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.Script.ScriptBody"] = "Write-Host hi",
        };

        var summary = curator.Curate(config);

        summary["scriptPreview"].Should().Be("Write-Host hi");
        summary.Should().NotContainKey("syntax", because: "absent keys are omitted, not emitted empty");
    }

    [Fact]
    public void StepGroup_curator_surfaces_foreach_and_maxparallelism()
    {
        var curator = new StepGroupConfigCurator();
        var config = new Dictionary<string, string>
        {
            ["Octopus.Action.ForEach.Collection"] = "#{instances}",
            ["Octopus.Action.MaxParallelism"]     = "2",
        };

        var summary = curator.Curate(config);

        summary["forEachCollection"].Should().Be("#{instances}");
        summary["maxParallelism"].Should().Be("2");
    }

    [Fact]
    public void Iis_curator_handles_both_native_and_imported_keys()
    {
        var curator = new IisStepConfigCurator();

        var native = curator.Curate(new Dictionary<string, string>
        {
            ["Kraken.IIS.SiteName"] = "Argosy",
            ["Kraken.IIS.WebRoot"]  = "C:\\inetpub\\argosy",
        });
        native["siteName"].Should().Be("Argosy");
        native["webRoot"].Should().Be("C:\\inetpub\\argosy");

        var imported = curator.Curate(new Dictionary<string, string>
        {
            ["Octopus.Action.IISWebSite.WebSiteName"]   = "Argosy",
            ["Octopus.Action.IISWebSite.PhysicalPath"]  = "C:\\inetpub\\argosy",
        });
        imported["siteName"].Should().Be("Argosy");
        imported["physicalPath"].Should().Be("C:\\inetpub\\argosy");
    }

    [Fact]
    public void Default_curator_emits_key_count_and_sample_but_no_values()
    {
        var curator = new DefaultStepConfigCurator();
        var config = new Dictionary<string, string>
        {
            ["Custom.Foo"]    = "secret-value-1",
            ["Custom.Bar"]    = "secret-value-2",
            ["Custom.Baz"]    = "secret-value-3",
            ["Custom.Qux"]    = "secret-value-4",
            ["Custom.Quux"]   = "secret-value-5",
            ["Custom.Corge"]  = "secret-value-6",
        };

        var summary = curator.Curate(config);

        summary["_configKeyCount"].Should().Be("6");
        summary["_configKeySample"].Should().Contain("Custom.Bar")
            .And.EndWith(", …", because: "more keys exist than the sample shows");
        // Crucially: no config VALUES leak — the default can't know which
        // are sensitive, so it emits key names only.
        summary.Values.Should().NotContain(v => v.Contains("secret-value"));
    }

    [Fact]
    public void Registry_routes_by_step_type_and_falls_back_to_default()
    {
        var registry = new StepConfigCuratorRegistry(
            new IStepConfigCurator[]
            {
                new ScriptStepConfigCurator(),
                new StepGroupConfigCurator(),
            },
            new DefaultStepConfigCurator());

        // Known type → dedicated curator.
        var script = registry.Curate("Octopus.Script", new Dictionary<string, string>
        {
            ["Octopus.Action.Script.Syntax"] = "Bash",
        });
        script["syntax"].Should().Be("Bash");

        // Alias type → same curator (Kraken.Script is a second [CuratesStepType]).
        var alias = registry.Curate("Kraken.Script", new Dictionary<string, string>
        {
            ["Octopus.Action.Script.Syntax"] = "PowerShell",
        });
        alias["syntax"].Should().Be("PowerShell");

        // Unknown type → default fallback (key count present).
        var unknown = registry.Curate("Custom.MysteryStep", new Dictionary<string, string>
        {
            ["a"] = "1",
            ["b"] = "2",
        });
        unknown["_configKeyCount"].Should().Be("2");
    }

    [Fact]
    public void Registry_tolerates_empty_step_type()
    {
        var registry = new StepConfigCuratorRegistry(
            [], new DefaultStepConfigCurator());

        var summary = registry.Curate("", new Dictionary<string, string> { ["x"] = "1" });

        summary["_configKeyCount"].Should().Be("1");
    }
}

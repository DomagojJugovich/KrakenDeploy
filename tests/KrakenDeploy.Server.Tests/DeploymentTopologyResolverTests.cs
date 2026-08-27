using FluentAssertions;
using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Core.Domain.Platform;
using Microsoft.Extensions.Configuration;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// BG1/T2 — the topology resolver: <c>Deployment:Topology</c> parsing, the
/// OnPrem default, and the named migration failure for a config still carrying
/// the removed <c>MultiAccount:Enabled</c> key (the old key's default-off would
/// otherwise silently turn a SaaS install single-tenant).
/// </summary>
public sealed class DeploymentTopologyResolverTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    [Fact]
    public void Defaults_to_OnPrem_when_unset()
        => DeploymentTopologyResolver.Resolve(Config())
            .Should().Be(DeploymentTopology.OnPrem);

    [Theory]
    [InlineData("OnPrem", DeploymentTopology.OnPrem)]
    [InlineData("OnPremBlueGreen", DeploymentTopology.OnPremBlueGreen)]
    [InlineData("onprembluegreen", DeploymentTopology.OnPremBlueGreen)]
    [InlineData("Saas", DeploymentTopology.Saas)]
    [InlineData("SAAS", DeploymentTopology.Saas)]
    public void Parses_names_case_insensitively(string raw, DeploymentTopology expected)
        => DeploymentTopologyResolver.Resolve(Config(("Deployment:Topology", raw)))
            .Should().Be(expected);

    [Theory]
    [InlineData("MultiNode")]
    [InlineData("2", "numeric values are refused — names only, so a config diff stays readable")]
    [InlineData("99")]
    public void Refuses_unrecognised_values_by_name(string raw, string? _ = null)
    {
        var act = () => DeploymentTopologyResolver.Resolve(Config(("Deployment:Topology", raw)));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Deployment:Topology*")
            .WithMessage($"*{raw}*");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false", "even Enabled=false fails — the key must be REMOVED, not zeroed")]
    public void Stale_MultiAccount_Enabled_fails_with_a_named_migration_message(
        string value, string? _ = null)
    {
        var act = () => DeploymentTopologyResolver.Resolve(
            Config(("MultiAccount:Enabled", value)));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MultiAccount:Enabled*")
            .WithMessage("*Deployment:Topology*")
            .WithMessage("*Saas*");
    }

    [Fact]
    public void Other_MultiAccount_keys_stay_valid()
        // BaseDomain/CacheSeconds still configure the Saas account layer — only
        // the removed Enabled switch is refused.
        => DeploymentTopologyResolver.Resolve(Config(
                ("MultiAccount:BaseDomain", "kraken.example"),
                ("Deployment:Topology", "Saas")))
            .Should().Be(DeploymentTopology.Saas);
}

/// <summary>
/// BG1/T4 — the Hangfire half of the non-additive guard is VERSION-CHECKED, not
/// guessed: the target schema version is discovered from the loaded
/// Hangfire.PostgreSql assembly's embedded Install.v{N}.sql scripts, so a
/// package bump moves it without any hardcoded number to forget.
/// </summary>
public sealed class HangfireSchemaInspectorTests
{
    [Fact]
    public void Target_schema_version_is_discovered_from_the_embedded_scripts()
        => HangfireSchemaInspector.GetTargetSchemaVersion().Should().BeGreaterThan(0,
            "Hangfire.PostgreSql ships Install.v{N}.sql embedded resources; zero would " +
            "mean the discovery pattern no longer matches this package version");
}

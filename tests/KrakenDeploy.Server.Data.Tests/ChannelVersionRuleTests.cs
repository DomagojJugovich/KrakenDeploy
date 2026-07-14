using FluentAssertions;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ChannelVersionRule"/> — the Octopus-semantics
/// channel version rule (NuGet range + pre-release-tag regex). No database.
/// </summary>
public sealed class ChannelVersionRuleTests
{
    [Fact]
    public void Empty_rule_matches_everything()
    {
        ChannelVersionRule.None.HasRules.Should().BeFalse();
        ChannelVersionRule.None.IsSatisfiedBy("1.2.3-anything").Should().BeTrue();
        ChannelVersionRule.Parse(null, null).HasRules.Should().BeFalse();
        ChannelVersionRule.Parse("   ", "").HasRules.Should().BeFalse();
    }

    [Theory]
    [InlineData("[1.0,2.0)", "1.5.0", true)]
    [InlineData("[1.0,2.0)", "2.0.0", false)]   // upper bound exclusive
    [InlineData("[1.0,2.0)", "0.9.0", false)]
    [InlineData("[1.0.0,)", "9.9.9", true)]     // open upper bound
    [InlineData("[1.2.0]", "1.2.0", true)]      // exact pin
    [InlineData("[1.2.0]", "1.2.1", false)]
    [InlineData("(1.0,2.0)", "1.0.0", false)]   // lower bound exclusive
    public void Range_is_enforced_with_NuGet_semantics(string range, string version, bool expected)
        => ChannelVersionRule.Parse(range, null).IsSatisfiedBy(version).Should().Be(expected);

    [Theory]
    [InlineData("^$", "1.0.0", true)]          // stable-only: stable matches
    [InlineData("^$", "1.0.0-beta", false)]    // stable-only: pre-release rejected
    [InlineData("^beta", "1.0.0-beta.1", true)]
    [InlineData("^beta", "1.0.0-alpha", false)]
    [InlineData("^beta", "1.0.0", false)]      // beta-required: stable rejected
    public void Tag_regex_is_enforced_against_the_prerelease_label(string tag, string version, bool expected)
        => ChannelVersionRule.Parse(null, tag).IsSatisfiedBy(version).Should().Be(expected);

    [Fact]
    public void Range_and_tag_both_apply()
    {
        var rule = ChannelVersionRule.Parse("[1.0,2.0)", "^$");
        rule.IsSatisfiedBy("1.5.0").Should().BeTrue();          // in range + stable
        rule.IsSatisfiedBy("2.5.0").Should().BeFalse();         // stable but out of range
        rule.IsSatisfiedBy("1.5.0-beta").Should().BeFalse();    // range excludes pre-release; tag requires stable
    }

    [Fact]
    public void Unparseable_version_fails_the_check_with_a_reason()
    {
        var reason = ChannelVersionRule.Parse("[1.0,2.0)", null).Check("not-a-version");
        reason.Should().NotBeNull();
    }

    [Theory]
    [InlineData("[1.0,2.0", null)]   // unbalanced bracket — invalid NuGet range
    [InlineData(null, "(")]          // unterminated group — invalid regex
    public void Malformed_rule_throws_FormatException(string? range, string? tag)
    {
        var act = () => ChannelVersionRule.Parse(range, tag);
        act.Should().Throw<FormatException>();
    }
}

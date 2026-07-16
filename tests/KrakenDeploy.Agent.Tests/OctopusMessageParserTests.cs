using System.Text;
using FluentAssertions;
using KrakenDeploy.Execution;
using Xunit;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Parser coverage for the <c>##octopus[setVariable]</c> marker, focused on the
/// T0-6 <c>sensitive</c> attribute (name/value are already base64 per the Octopus
/// convention; the sensitive flag is a plain bool).
/// </summary>
public sealed class OctopusMessageParserTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void SetVariable_without_sensitive_flag_is_not_sensitive()
    {
        var line = $"##octopus[setVariable name='{B64("Url")}' value='{B64("https://x")}']";
        var msg = OctopusMessageParser.TryParse(line);
        var v = msg.Should().BeOfType<SetVariableMessage>().Subject;
        v.Name.Should().Be("Url");
        v.Value.Should().Be("https://x");
        v.Sensitive.Should().BeFalse();
    }

    [Fact]
    public void SetVariable_with_sensitive_True_marks_sensitive()
    {
        var line = $"##octopus[setVariable name='{B64("Token")}' value='{B64("s3cr3t")}' sensitive='True']";
        var msg = OctopusMessageParser.TryParse(line);
        var v = msg.Should().BeOfType<SetVariableMessage>().Subject;
        v.Name.Should().Be("Token");
        v.Value.Should().Be("s3cr3t");
        v.Sensitive.Should().BeTrue();
    }

    [Fact]
    public void SetVariable_with_sensitive_False_is_not_sensitive()
    {
        var line = $"##octopus[setVariable name='{B64("Token")}' value='{B64("s3cr3t")}' sensitive='False']";
        var v = OctopusMessageParser.TryParse(line).Should().BeOfType<SetVariableMessage>().Subject;
        v.Sensitive.Should().BeFalse();
    }

    [Fact]
    public void SetOutputVariable_alias_also_reads_sensitive_flag()
    {
        var line = $"##octopus[setOutputVariable name='{B64("K")}' value='{B64("V")}' sensitive='true']";
        var v = OctopusMessageParser.TryParse(line).Should().BeOfType<SetVariableMessage>().Subject;
        v.Sensitive.Should().BeTrue();
    }
}

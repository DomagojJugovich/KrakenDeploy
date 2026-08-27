using FluentAssertions;
using KrakenDeploy.Cli.Commands;

namespace KrakenDeploy.Cli.Tests;

public sealed class ReleaseCommandsPromptedVariableTests
{
    [Fact]
    public void ParsePromptedValues_supports_repeatable_values_and_embedded_equals()
    {
        var result = ReleaseCommands.ParsePromptedValues(["Greeting=hello", "Connection=a=b=c"]);

        result.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Greeting"] = "hello",
            ["Connection"] = "a=b=c",
        });
    }

    [Fact]
    public void ParsePromptedValues_rejects_a_missing_name_or_separator()
    {
        var act = () => ReleaseCommands.ParsePromptedValues(["invalid"]);

        act.Should().Throw<ArgumentException>().WithMessage("*Name=Value*");
    }
}

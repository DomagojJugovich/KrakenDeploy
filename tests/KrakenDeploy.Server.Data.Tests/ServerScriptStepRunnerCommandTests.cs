using FluentAssertions;
using KrakenDeploy.Server.Transport;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// C5/T1-20 parity for the server-orchestrated script runner: same BOM + edition
/// rules as <c>Steps.Common.ScriptRunner</c> (the two runners deliberately
/// duplicate the write/command helpers). Pure unit tests — no Postgres.
/// </summary>
public sealed class ServerScriptStepRunnerCommandTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Theory]
    [InlineData("PowerShell")]
    [InlineData("")]
    public void PowerShell_scripts_are_written_utf8_with_bom(string syntax)
        => ServerScriptStepRunner.EncodingForSyntax(syntax).GetPreamble().Should().Equal(Utf8Bom);

    [Theory]
    [InlineData("bash")]
    [InlineData("python")]
    public void Non_powershell_scripts_are_written_without_a_bom(string syntax)
        => ServerScriptStepRunner.EncodingForSyntax(syntax).GetPreamble().Should().BeEmpty();

    [Theory]
    [InlineData(null,      true,  "powershell.exe")]
    [InlineData("Desktop", true,  "powershell.exe")]
    [InlineData("Core",    true,  "pwsh")]
    [InlineData(null,      false, "pwsh")]
    [InlineData("Desktop", false, "pwsh")]
    public void PowerShell_edition_resolves_to_the_right_executable(
        string? edition, bool isWindows, string expectedExe)
        => ServerScriptStepRunner.BuildCommand("s.ps1", "PowerShell", edition, isWindows).exe
            .Should().Be(expectedExe);
}

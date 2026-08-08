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

    [Fact]
    public void WriteScriptFile_creates_an_isolated_per_dispatch_subdirectory()
    {
        var pathA = ServerScriptStepRunner.WriteScriptFile("Write-Host 'a'", "PowerShell");
        var pathB = ServerScriptStepRunner.WriteScriptFile("Write-Host 'b'", "PowerShell");
        try
        {
            File.Exists(pathA).Should().BeTrue();
            File.Exists(pathB).Should().BeTrue();

            var dirA = Path.GetDirectoryName(pathA)!;
            var dirB = Path.GetDirectoryName(pathB)!;
            dirA.Should().NotBe(dirB,
                "each dispatch must get its own subdirectory, not the shared temp root");
            dirA.Should().NotBe(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                "the script must not land directly in the shared temp root");
        }
        finally
        {
            TryDeleteDir(Path.GetDirectoryName(pathA)!);
            TryDeleteDir(Path.GetDirectoryName(pathB)!);
        }
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}

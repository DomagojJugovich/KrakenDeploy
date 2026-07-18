using FluentAssertions;
using KrakenDeploy.Steps.Common;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// C5/T1-20 — the shared <see cref="ScriptRunner"/> must write PowerShell scripts
/// UTF-8 WITH a BOM (so Windows PowerShell 5.1 doesn't read Croatian as ANSI) and
/// default an unspecified PowerShell edition to Windows PowerShell on Windows
/// (pwsh is not on stock Windows Server). Both are pure, OS-parameterised helpers
/// so the matrix is deterministic on any CI OS.
/// </summary>
public sealed class ScriptRunnerCommandTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Theory]
    [InlineData("PowerShell")]
    [InlineData("powershell")]
    [InlineData("")]            // unknown/blank defaults to PowerShell
    public void PowerShell_scripts_are_written_utf8_with_bom(string syntax)
        => ScriptRunner.EncodingForSyntax(syntax).GetPreamble().Should().Equal(Utf8Bom);

    [Theory]
    [InlineData("bash")]
    [InlineData("python")]
    [InlineData("csharp")]
    [InlineData("fsharp")]
    public void Non_powershell_scripts_are_written_without_a_bom(string syntax)
        => ScriptRunner.EncodingForSyntax(syntax).GetPreamble().Should().BeEmpty();

    [Theory]
    // Windows: default (null/blank) and explicit Desktop → Windows PowerShell.
    [InlineData(null,      true,  "powershell.exe")]
    [InlineData("",        true,  "powershell.exe")]
    [InlineData("Desktop", true,  "powershell.exe")]
    [InlineData("desktop", true,  "powershell.exe")]
    // Windows: only an explicit Core edition → pwsh.
    [InlineData("Core",    true,  "pwsh")]
    [InlineData("core",    true,  "pwsh")]
    // Off Windows: powershell.exe doesn't exist, so everything runs under pwsh.
    [InlineData(null,      false, "pwsh")]
    [InlineData("Desktop", false, "pwsh")]
    [InlineData("Core",    false, "pwsh")]
    public void PowerShell_edition_resolves_to_the_right_executable(
        string? edition, bool isWindows, string expectedExe)
        => ScriptRunner.BuildCommand("s.ps1", "PowerShell", edition, isWindows).exe
            .Should().Be(expectedExe);

    [Theory]
    [InlineData("bash",   "bash")]
    [InlineData("python", "python")]
    public void Non_powershell_syntaxes_pick_their_interpreter_regardless_of_os(
        string syntax, string expectedExe)
    {
        ScriptRunner.BuildCommand("s", syntax, null, isWindows: true).exe.Should().Be(expectedExe);
        ScriptRunner.BuildCommand("s", syntax, null, isWindows: false).exe.Should().Be(expectedExe);
    }

    // C5/T1-20 output-direction round-trip: with the runner's UTF-8
    // StandardOutputEncoding + the child emitting UTF-8, Croatian survives capture.
    // Windows-only (uses powershell.exe / Windows PowerShell 5.1); no-op elsewhere.
    // Unicode escapes keep the assertion independent of this file's own encoding.
    [Fact]
    public async Task PowerShell_output_round_trips_croatian_as_utf8_on_windows()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        // Escaped (not raw) so this literal is immune to this .cs file's own encoding.
        const string croatian = "\u010D\u0107\u0161\u017E\u0111"; // c-caron c-acute s-caron z-caron d-bar
        var captured = new List<string>();
        var gate = new object();

        var exit = await new ScriptRunner().RunAndReturnExitCodeAsync(
            scriptBody: "try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }\r\n"
                        + $"Write-Output '{croatian}'",
            syntax: "PowerShell",
            workingDirectory: Path.GetTempPath(),
            environmentVariables: new Dictionary<string, string>(),
            onOutput: (_, line) => { lock (gate) { captured.Add(line); } return Task.CompletedTask; },
            ct: CancellationToken.None,
            powerShellEdition: "Desktop");

        exit.Should().Be(0);
        string.Join("\n", captured).Should().Contain(croatian,
            "the parent decodes stdout as UTF-8 and the child emits UTF-8");
    }
}

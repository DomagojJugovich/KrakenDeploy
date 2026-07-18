using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Steps.Script;

namespace KrakenDeploy.Steps.Script.Tests;

/// <summary>
/// Tests for the Phase D-8.4 Kraken.Script / Octopus.Script step-package
/// port. Covers <c>CanHandle</c> (both step types accepted, case-insensitive),
/// the package's static preamble builders (deterministic output, used by all
/// language runtimes), and the built archive shape.
/// <para>
/// Process-spawn paths (PowerShell / bash / dotnet-script execution) are NOT
/// exercised here — they need real interpreters installed and so live in
/// integration tests. The handler-to-runner wiring is verified indirectly via
/// the preamble builders (the only piece of handler logic that isn't a thin
/// pass-through to <c>ScriptRunner.RunAsync</c>).
/// </para>
/// </summary>
public sealed class ScriptPackageTests
{
    [Theory]
    [InlineData("Kraken.Script",  true)]
    [InlineData("Octopus.Script", true)]
    [InlineData("kraken.script",  true)]
    [InlineData("OCTOPUS.SCRIPT", true)]
    [InlineData("Octopus.Manual", false)]
    [InlineData("",               false)]
    public void CanHandle_recognises_both_step_types_case_insensitively(string stepType, bool expected)
        => new ScriptStepHandler().CanHandle(stepType).Should().Be(expected);

    [Fact]
    public void Handler_requires_a_package()
        => new ScriptStepHandler().RequiresPackage.Should().BeTrue(
            "scripts execute against the extracted package's working directory");

    [Fact]
    public void PowerShell_preamble_contains_OctopusParameters_and_helpers()
    {
        var ps = ScriptStepHandler.BuildPowerShellPreamble(
            variables: new Dictionary<string, string> { ["Greeting"] = "hello" },
            arrayVariables: new Dictionary<string, string[]> { ["Hosts"] = ["a", "b"] },
            environmentName: "Production",
            deploymentId: Guid.Empty);

        ps.Should().Contain("$OctopusParameters = [ordered]@{")
          .And.Contain("'Octopus.Environment.Name' = 'Production'")
          .And.Contain("'Greeting' = 'hello'")
          .And.Contain("'Hosts' = @('a', 'b')")
          .And.Contain("function Write-KrakenInfo")
          .And.Contain("function Set-OctopusVariable");
    }

    [Fact]
    public void PowerShell_preamble_forces_utf8_output_before_anything_else()
    {
        // C5/T1-20: the FIRST statement switches the console to UTF-8 so Croatian
        // (č ć š ž đ) in Write-Host / native output isn't emitted as the OEM code
        // page under Windows PowerShell 5.1.
        var ps = ScriptStepHandler.BuildPowerShellPreamble(
            variables: new Dictionary<string, string>(),
            arrayVariables: new Dictionary<string, string[]>(),
            environmentName: "Prod",
            deploymentId: Guid.Empty);

        ps.Should().StartWith(
            "try { $OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }",
            "UTF-8 output must be forced before any script output is written");
    }

    [Fact]
    public void PowerShell_preamble_escapes_embedded_quotes_in_variable_values()
    {
        // PowerShell single-quoted string literals escape ' by doubling it.
        var ps = ScriptStepHandler.BuildPowerShellPreamble(
            variables: new Dictionary<string, string> { ["Quote"] = "it's tricky" },
            arrayVariables: new Dictionary<string, string[]>(),
            environmentName: "Dev",
            deploymentId: Guid.Empty);

        ps.Should().Contain("'Quote' = 'it''s tricky'",
            "embedded single quotes must be doubled per PowerShell's literal-string escape rules");
    }

    [Fact]
    public void Bash_preamble_defines_set_and_get_octopusvariable()
    {
        var bash = ScriptStepHandler.BuildBashPreamble();
        bash.Should().Contain("get_octopusvariable()")
            .And.Contain("set_octopusvariable()")
            .And.Contain("new_octopusartifact()")
            .And.Contain("##octopus[setVariable");
    }

    [Fact]
    public void Python_preamble_defines_octopusparameters_dict()
    {
        var py = ScriptStepHandler.BuildPythonPreamble();
        py.Should().Contain("OctopusParameters = octopusvariables")
            .And.Contain("def set_octopusvariable(name, value, sensitive=False):")
            .And.Contain("def new_octopusartifact(path, name=None):");
    }

    [Fact]
    public void CSharp_preamble_builds_OctopusParameters_dictionary_from_env()
    {
        var cs = ScriptStepHandler.BuildCSharpPreamble();
        cs.Should().Contain("var OctopusParameters = Environment.GetEnvironmentVariables()")
            .And.Contain("void SetOctopusVariable(string name, string value, bool sensitive = false)")
            .And.Contain("void NewOctopusArtifact(string path, string? name = null)");
    }

    [Fact]
    public void FSharp_preamble_exposes_OctopusParameters_map_and_helpers()
    {
        var fs = ScriptStepHandler.BuildFSharpPreamble();
        fs.Should().Contain("let OctopusParameters =")
            .And.Contain("let setOctopusVariable")
            .And.Contain("let newOctopusArtifact");
    }

    // ── Built archive ──────────────────────────────────────────────────────

    [Fact]
    public void Built_archive_has_correct_manifest_with_both_step_types()
    {
        var path = FindBuiltArchive();
        path.Should().NotBeNull(
            "the pack target must produce kraken.script-1.0.0.kdeploy-step");

        using var fs  = File.OpenRead(path!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        using var r   = new StreamReader(
            zip.GetEntry(StepPackageFiles.ManifestFileName)!.Open());
        var manifest = StepPackageManifestJson.Deserialize(r.ReadToEnd());

        manifest.Id.Should().Be("kraken.script");
        manifest.Version.Should().Be("1.0.0");
        manifest.ExecutorTypeName.Should().Be(typeof(ScriptStepHandler).FullName!);

        manifest.StepTypes.Should().HaveCount(2,
            "the multi-step-type comma split in KrakenStepPackage.targets must " +
            "produce a JSON array with both names");
        manifest.StepTypes.Should().Contain("Kraken.Script");
        manifest.StepTypes.Should().Contain("Octopus.Script");
    }

    private static string? FindBuiltArchive()
    {
        var here      = AppContext.BaseDirectory;
        var binRoot = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.Script", "bin"));
        // Configuration-agnostic: CI builds Release, local builds Debug — locate
        // the packed archive under bin/<Config>/<tfm>/ wherever it landed.
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "kraken.script-1.0.0.kdeploy-step",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}

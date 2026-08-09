using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Steps.OctopusTentaclePackage;

namespace KrakenDeploy.Steps.OctopusTentaclePackage.Tests;

/// <summary>
/// Archive-layout tests for the produced
/// <c>octopus.tentaclepackage-*.kdeploy-step</c> — the package shipped with
/// no coverage of its packed shape until the 2026-08-09 version-bump breakage
/// showed the gap (handler behaviour lives in
/// <see cref="OctopusTentaclePackagePackageTests"/>). Mirrors the pattern in
/// the other Steps.*.Tests suites so any broken pack target shows up here,
/// not at runtime on a real agent.
/// </summary>
public sealed class OctopusTentaclePackageArchiveTests
{
    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.tentaclepackage-<version>.kdeploy-step");
    }

    [Fact]
    public void Built_archive_contains_a_well_formed_manifest_and_executor_DLL()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry(StepPackageFiles.ManifestFileName);
        manifestEntry.Should().NotBeNull();

        using var reader = new StreamReader(manifestEntry!.Open());
        var manifest = StepPackageManifestJson.Deserialize(reader.ReadToEnd());

        manifest.Id.Should().Be("octopus.tentaclepackage");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().HaveCount(2,
            "one handler serves the full Octopus shape and the Kraken.DeployPackage alias");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TentaclePackage");
        manifest.StepTypes.Should().Contain(t => t.Id == "Kraken.DeployPackage");
        manifest.ExecutorTypeName.Should().Be(typeof(OctopusTentaclePackageStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.OctopusTentaclePackage.dll");

        zip.GetEntry($"executor/{manifest.ExecutorAssembly}").Should().NotBeNull();
    }

    [Fact]
    public void Built_archive_bundles_Octostache_and_Xdt_runtime_DLLs()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        zip.GetEntry("executor/Octostache.dll").Should().NotBeNull(
            "#{Var} placeholders inside config strings (e.g. "
            + "CustomInstallationDirectory) are resolved via Octostache");
        zip.GetEntry("executor/Microsoft.Web.XmlTransform.dll").Should().NotBeNull(
            "Microsoft.Web.Xdt drives Octopus.Features.ConfigurationTransforms — "
            + "the agent host does NOT reference it, so the ALC delegation "
            + "fallback can't save us if it's missing");
    }

    [Fact]
    public void Built_archive_excludes_agent_hosted_runtime_DLLs()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entries = zip.Entries.Select(e => e.FullName).ToArray();

        entries.Should().NotContain(e => e.EndsWith("/KrakenDeploy.Contracts.dll", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.EndsWith("/Google.Protobuf.dll", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.Contains("/Grpc.", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.EndsWith("/Microsoft.Extensions.Logging.Abstractions.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindBuiltArchive()
    {
        var here = AppContext.BaseDirectory;
        var binRoot = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.OctopusTentaclePackage", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.tentaclepackage-*.kdeploy-step",
                SearchOption.AllDirectories)
                .OrderByDescending(p => Version.Parse(ArchiveVersion(p)))
                .FirstOrDefault()
            : null;
    }

    // "<id>-<version>.kdeploy-step" -> "<version>" (the id itself may contain dashes).
    private static string ArchiveVersion(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem[(stem.LastIndexOf('-') + 1)..];
    }
}

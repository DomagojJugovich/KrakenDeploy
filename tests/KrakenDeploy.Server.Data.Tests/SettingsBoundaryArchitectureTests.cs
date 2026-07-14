using FluentAssertions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Architecture guard: the unified <c>settings</c> table is NOT
/// <c>ISpaceScoped</c>, so a stray direct query could read another Space's AI
/// settings unscoped. All access must funnel through <c>SettingsService</c>
/// (which cages by scope). There is deliberately no <c>DbSet&lt;Setting&gt;</c>
/// on the context; the only place that may call <c>Set&lt;Setting&gt;()</c> is
/// <c>SettingsService.cs</c>. This source scan fails if any other source file
/// under <c>src/</c> references it.
/// </summary>
public sealed class SettingsBoundaryArchitectureTests
{
    private const string ForbiddenToken = "Set<Setting>";
    private const string AllowedFile = "SettingsService.cs";

    [Fact]
    public void Only_SettingsService_references_the_settings_DbSet()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        Directory.Exists(srcRoot).Should().BeTrue($"expected a src directory at {srcRoot}");

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(ForbiddenToken, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, AllowedFile, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            $"'{ForbiddenToken}' must appear only in {AllowedFile}; every other component " +
            "must go through SettingsService so the non-Space-scoped settings table can't be " +
            "queried without scope caging. Offending file(s): " + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KrakenDeploy.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (KrakenDeploy.sln) above " +
            AppContext.BaseDirectory + " — the settings-DbSet boundary scan cannot run.");
    }
}

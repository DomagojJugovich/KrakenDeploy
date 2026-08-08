using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using KrakenDeploy.Contracts.Steps;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// SC1-d schema lint: every step type a <c>steps/</c> package project claims
/// must ship a parseable, structurally sound per-type UI schema at
/// <c>ui-schemas/{typeId}.json</c> — and nothing extra. This is the guard
/// that keeps "what runs" (csproj claims), "what renders" (schema files) and
/// "what the picker shows" (per-type metadata) from drifting apart again the
/// way the pre-consolidation triple lists did.
/// </summary>
public sealed class StepPackageSchemaLintTests
{
    /// <summary>Every widget id declared on <see cref="StepUiWidgets"/>, by reflection — no hand-maintained copy to drift.</summary>
    private static readonly HashSet<string> KnownWidgets = typeof(StepUiWidgets)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    public static TheoryData<string> StepProjectDirs()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRoot(), "steps")))
        {
            // Package projects import the pack targets; Steps.Common (shared
            // lib) and anything else without the import is out of scope. An
            // actual <Import> element is required — a comment mentioning the
            // targets file must not count.
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj is not null
                && XDocument.Load(csproj).Descendants("Import").Any(i =>
                    (i.Attribute("Project")?.Value ?? "")
                        .EndsWith("KrakenStepPackage.targets", StringComparison.OrdinalIgnoreCase)))
            {
                data.Add(Path.GetFileName(dir));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(StepProjectDirs))]
    public void Every_claimed_type_has_exactly_one_valid_schema_file(string projectDirName)
    {
        var projectDir = Path.Combine(RepoRoot(), "steps", projectDirName);
        var claimed    = ClaimedTypeIds(projectDir);
        claimed.Should().NotBeEmpty(
            $"{projectDirName} imports the pack targets, so it must declare step types");

        var schemasDir = Path.Combine(projectDir, "ui-schemas");
        Directory.Exists(schemasDir).Should().BeTrue(
            $"{projectDirName} must ship per-type schemas under ui-schemas/");

        var files = Directory.GetFiles(schemasDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = claimed.Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        files.Should().BeEquivalentTo(expected,
            $"{projectDirName}: ui-schemas/ must contain exactly one {{typeId}}.json per claimed type " +
            "(a missing file means an unrenderable step; an orphan file means a dead schema)");

        foreach (var typeId in expected)
        {
            AssertSchemaIsSound(
                Path.Combine(schemasDir, $"{typeId}.json"), $"{projectDirName}/{typeId}");
        }
    }

    // ── Structural soundness ────────────────────────────────────────────────

    private static void AssertSchemaIsSound(string path, string label)
    {
        StepUiSchema schema;
        try
        {
            schema = StepUiSchemaJson.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new Xunit.Sdk.XunitException($"{label}: schema failed to parse — {ex.Message}");
        }

        schema.Id.Should().NotBeNullOrWhiteSpace($"{label}: schema root id");
        schema.Title.Should().NotBeNullOrWhiteSpace($"{label}: schema root title");

        var groupIds = schema.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, field) in schema.Properties)
        {
            KnownWidgets.Should().Contain(field.Widget,
                $"{label}: field '{key}' uses an unknown widget '{field.Widget}'");

            if (field.Widget == StepUiWidgets.Select)
            {
                field.EnumValues.Should().NotBeEmpty(
                    $"{label}: select field '{key}' needs enumValues");
            }

            if (field.VisibleWhen is not null)
            {
                schema.Properties.Keys.Should().Contain(field.VisibleWhen.Field,
                    $"{label}: field '{key}' has visibleWhen referencing " +
                    $"'{field.VisibleWhen.Field}', which is not a property of the schema");
            }

            if (!string.IsNullOrEmpty(field.Group))
            {
                groupIds.Should().Contain(field.Group,
                    $"{label}: field '{key}' references undeclared group '{field.Group}'");
            }
        }
    }

    // ── csproj parsing ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the claimed type ids from a package project: KrakenStepType
    /// items when present, else the legacy comma-list property — mirroring
    /// the pack target's precedence.
    /// </summary>
    private static IReadOnlyList<string> ClaimedTypeIds(string projectDir)
    {
        var csproj = Directory.GetFiles(projectDir, "*.csproj").Single();
        var doc    = XDocument.Load(csproj);

        var items = doc.Descendants("KrakenStepType")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        if (items.Count > 0) { return items; }

        var legacy = doc.Descendants("KrakenStepPackageStepTypes").FirstOrDefault()?.Value ?? "";
        return legacy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KrakenDeploy.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate KrakenDeploy.sln above {AppContext.BaseDirectory}");
    }
}

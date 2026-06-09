using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="ProcessService.ImportDeploymentProcessAsync"/>
/// against a real Postgres database. Uses the shared <see cref="PostgresFixture"/>.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ProcessServiceImportTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Import_appends_steps_to_an_empty_project_process()
    {
        var projectId = await SeedProjectAsync();
        var svc = new ProcessService(postgres);

        var result = await svc.ImportDeploymentProcessAsync(
            projectId,
            json: LoadTestData("argosy-process.json"),
            replace: false);

        result.Imported.Should().BeGreaterThan(0);
        result.ReplacedExisting.Should().Be(0);

        var process = await svc.GetAsync(projectId);
        process.Should().NotBeNull();
        process!.Steps.Should().HaveCount(result.Imported);
        process.Steps.Select(s => s.SortOrder).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Import_with_replace_clears_existing_steps_first()
    {
        var projectId = await SeedProjectAsync();
        var svc = new ProcessService(postgres);

        // Seed one step manually so we have something to be replaced.
        await svc.AddStepAsync(projectId, "Existing", "Kraken.Script", "ManualPkg",
            targetRoles: [], config: []);

        var result = await svc.ImportDeploymentProcessAsync(
            projectId,
            json: LoadTestData("argosy-process.json"),
            replace: true);

        result.ReplacedExisting.Should().Be(1);
        result.Imported.Should().BeGreaterThan(0);

        var process = await svc.GetAsync(projectId);
        process!.Steps.Should().HaveCount(result.Imported);
        process.Steps.Should().NotContain(s => s.Name == "Existing",
            "the existing step was replaced");
    }

    [Fact]
    public async Task Import_without_replace_appends_after_existing_steps()
    {
        var projectId = await SeedProjectAsync();
        var svc = new ProcessService(postgres);

        await svc.AddStepAsync(projectId, "Existing", "Kraken.Script", "ManualPkg",
            targetRoles: [], config: []);

        var result = await svc.ImportDeploymentProcessAsync(
            projectId,
            json: LoadTestData("argosy-process.json"),
            replace: false);

        var process = await svc.GetAsync(projectId);
        process!.Steps.Should().HaveCount(1 + result.Imported);
        process.Steps.OrderBy(s => s.SortOrder).First().Name
            .Should().Be("Existing", "the pre-existing step keeps its leading position");
    }

    [Fact]
    public async Task Import_preserves_octopus_property_keys_verbatim()
    {
        var projectId = await SeedProjectAsync();
        var svc = new ProcessService(postgres);

        await svc.ImportDeploymentProcessAsync(
            projectId,
            json: LoadTestData("webargosy-virtual-app.json"),
            replace: false);

        var process = await svc.GetAsync(projectId);
        process.Should().NotBeNull();

        var iisStep = process!.Steps.FirstOrDefault(s => s.StepType == "Octopus.IIS");
        iisStep.Should().NotBeNull("the webargosy process is expected to contain an Octopus.IIS step");
        iisStep!.Config.Keys.Should().Contain(k => k.StartsWith("Octopus.Action.IISWebSite.",
            StringComparison.Ordinal),
            "the Octopus property keys must be preserved verbatim — no key translation");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = postgres.CreateContext();
        var project = new Project
        {
            Name = $"ImportTest-{Guid.NewGuid():N}",
            Slug = $"importtest-{Guid.NewGuid():N}",
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static string LoadTestData(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        return File.ReadAllText(path);
    }
}

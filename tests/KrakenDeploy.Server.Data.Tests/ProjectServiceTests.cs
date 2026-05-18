using FluentAssertions;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

[Collection("Postgres")]
public class ProjectServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // ── Slugify — pure static method, no DB needed ─────────────────────────

    [Theory]
    [InlineData("My Project",           "my-project")]
    [InlineData("Hello   World",        "hello-world")]
    [InlineData("C# app / v2.0!",       "c-app-v2-0")]
    [InlineData("my-project",           "my-project")]
    [InlineData("My_Project",           "my-project")]
    [InlineData("  leading-trailing  ", "leading-trailing")]
    [InlineData("hello--world",         "hello-world")]
    [InlineData("123",                  "123")]
    [InlineData("",                     "")]
    public void Slugify_produces_valid_slug(string input, string expected)
        => ProjectService.Slugify(input).Should().Be(expected);

    [Fact]
    public void Slugify_truncates_output_at_64_characters()
        => ProjectService.Slugify(new string('a', 100)).Should().HaveLength(64);

    [Fact]
    public void Slugify_does_not_truncate_below_64_characters()
        => ProjectService.Slugify(new string('a', 40)).Should().HaveLength(40);

    [Fact]
    public void Slugify_throws_for_null_input()
        => ((Action)(() => ProjectService.Slugify(null!)))
            .Should().Throw<ArgumentNullException>();

    // ── CRUD ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_persists_project_with_generated_id()
    {
        await using var db = postgres.CreateContext();
        var svc = new ProjectService(postgres);

        var project = await svc.CreateAsync("Smoke Project", $"smoke-{Guid.NewGuid():N}", "desc");

        project.Id.Should().NotBe(Guid.Empty);
        project.Name.Should().Be("Smoke Project");
        project.Description.Should().Be("desc");
    }

    [Fact]
    public async Task Create_throws_on_duplicate_slug()
    {
        await using var db = postgres.CreateContext();
        var svc = new ProjectService(postgres);
        var slug = $"dup-{Guid.NewGuid():N}";

        await svc.CreateAsync("First", slug, null);

        var act = async () => await svc.CreateAsync("Second", slug, null);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already taken*");
    }

    [Fact]
    public async Task GetAllAsync_returns_projects_ordered_by_name()
    {
        await using var db = postgres.CreateContext();
        var svc = new ProjectService(postgres);
        var suffix = Guid.NewGuid().ToString("N");

        await svc.CreateAsync($"Zzz-{suffix}", $"zzz-{suffix}", null);
        await svc.CreateAsync($"Aaa-{suffix}", $"aaa-{suffix}", null);
        await svc.CreateAsync($"Mmm-{suffix}", $"mmm-{suffix}", null);

        var all = await svc.GetAllAsync();
        var ours = all.Where(p => p.Slug.EndsWith(suffix, StringComparison.Ordinal)).ToList();

        ours.Select(p => p.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Update_modifies_name_and_description()
    {
        await using var db = postgres.CreateContext();
        var svc = new ProjectService(postgres);
        var slug = $"upd-{Guid.NewGuid():N}";

        var created = await svc.CreateAsync("Original", slug, null);
        var updated = await svc.UpdateAsync(created.Id, "Renamed", slug, "new desc");

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Renamed");
        updated.Description.Should().Be("new desc");
    }

    [Fact]
    public async Task Delete_removes_project_and_returns_true()
    {
        await using var db = postgres.CreateContext();
        var svc = new ProjectService(postgres);
        var slug = $"del-{Guid.NewGuid():N}";

        var created = await svc.CreateAsync("ToDelete", slug, null);
        var deleted = await svc.DeleteAsync(created.Id);

        deleted.Should().BeTrue();
        var all = await svc.GetAllAsync();
        all.Should().NotContain(p => p.Id == created.Id);
    }

    [Fact]
    public async Task Delete_returns_false_for_nonexistent_id()
    {
        await using var db = postgres.CreateContext();
        var svc = new ProjectService(postgres);

        var result = await svc.DeleteAsync(Guid.NewGuid());

        result.Should().BeFalse();
    }
}

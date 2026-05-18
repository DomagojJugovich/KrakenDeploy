using FluentAssertions;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

[Collection("Postgres")]
public class EnvironmentServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Create_assigns_ascending_sort_orders()
    {
        await using var db = postgres.CreateContext();
        var svc = new EnvironmentService(postgres);
        var s = Guid.NewGuid().ToString("N");

        var dev  = await svc.CreateAsync($"Dev-{s}",  $"dev-{s}");
        var test = await svc.CreateAsync($"Test-{s}", $"test-{s}");
        var prod = await svc.CreateAsync($"Prod-{s}", $"prod-{s}");

        test.SortOrder.Should().BeGreaterThan(dev.SortOrder);
        prod.SortOrder.Should().BeGreaterThan(test.SortOrder);
    }

    [Fact]
    public async Task GetAllOrdered_returns_environments_sorted_by_sort_order()
    {
        await using var db = postgres.CreateContext();
        var svc = new EnvironmentService(postgres);
        var s = Guid.NewGuid().ToString("N");

        var dev  = await svc.CreateAsync($"Dev-{s}",  $"dev-{s}");
        var prod = await svc.CreateAsync($"Prod-{s}", $"prod-{s}");

        var all  = await svc.GetAllOrderedAsync();
        var ours = all.Where(e => e.Slug.EndsWith(s, StringComparison.Ordinal)).ToList();

        ours.Should().HaveCount(2);
        ours[0].Id.Should().Be(dev.Id,  because: "Dev was created first and has lower SortOrder");
        ours[1].Id.Should().Be(prod.Id, because: "Prod was created second and has higher SortOrder");
    }

    [Fact]
    public async Task MoveUp_places_environment_before_its_predecessor()
    {
        await using var db = postgres.CreateContext();
        var svc = new EnvironmentService(postgres);
        var s = Guid.NewGuid().ToString("N");

        var first  = await svc.CreateAsync($"First-{s}",  $"first-{s}");
        var second = await svc.CreateAsync($"Second-{s}", $"second-{s}");

        await svc.MoveUpAsync(second.Id);

        var all  = await svc.GetAllOrderedAsync();
        var ours = all.Where(e => e.Slug.EndsWith(s, StringComparison.Ordinal)).ToList();

        ours[0].Id.Should().Be(second.Id, because: "MoveUp should place Second before First");
        ours[1].Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task MoveDown_places_environment_after_its_successor()
    {
        await using var db = postgres.CreateContext();
        var svc = new EnvironmentService(postgres);
        var s = Guid.NewGuid().ToString("N");

        var first  = await svc.CreateAsync($"First-{s}",  $"first-{s}");
        var second = await svc.CreateAsync($"Second-{s}", $"second-{s}");

        await svc.MoveDownAsync(first.Id);

        var all  = await svc.GetAllOrderedAsync();
        var ours = all.Where(e => e.Slug.EndsWith(s, StringComparison.Ordinal)).ToList();

        ours[0].Id.Should().Be(second.Id, because: "MoveDown on First should place Second before it");
        ours[1].Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task Create_throws_on_duplicate_slug()
    {
        await using var db = postgres.CreateContext();
        var svc = new EnvironmentService(postgres);
        var slug = $"dup-{Guid.NewGuid():N}";

        await svc.CreateAsync("Env", slug);

        var act = async () => await svc.CreateAsync("Env2", slug);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already taken*");
    }
}

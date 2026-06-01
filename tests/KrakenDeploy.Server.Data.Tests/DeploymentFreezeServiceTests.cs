using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Freezes;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.F.2 — <see cref="DeploymentFreezeService"/>.
/// The match logic in <c>FindBlockingFreezeAsync</c> is the hot path the
/// DeploymentWorker calls on every dispatch, so every scope dimension
/// (window, project, environment, tenant tag) gets a positive + negative
/// test here.
/// </summary>
[Collection("Postgres")]
public sealed class DeploymentFreezeServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.DeploymentFreezes.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── CRUD basics ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_persists_and_returns_row()
    {
        var svc = NewSvc();
        var input = SampleFreeze();

        var created = await svc.CreateAsync(input);

        created.Id.Should().NotBe(Guid.Empty);
        created.SpaceId.Should().Be(WellKnown.DefaultSpaceId);

        await using var db = postgres.CreateContext();
        (await db.DeploymentFreezes.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_validates_window()
    {
        var svc = NewSvc();
        var input = SampleFreeze();
        input.EndUtc = input.StartUtc; // not strictly after

        var act = async () => await svc.CreateAsync(input);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*strictly after*");
    }

    [Fact]
    public async Task UpdateAsync_replaces_scope_lists_completely()
    {
        // The contract is "replace, not merge" — clear semantics for the
        // operator who edits the project list down from 5 to 2.
        var svc = NewSvc();
        var input = SampleFreeze();
        input.ProjectIds = [Guid.NewGuid(), Guid.NewGuid()];
        var created = await svc.CreateAsync(input);

        var edit = SampleFreeze();
        edit.ProjectIds = [Guid.NewGuid()];
        var updated = await svc.UpdateAsync(created.Id, edit);

        updated!.ProjectIds.Should().HaveCount(1,
            "update replaces the list; merging would create stale ID buildup");
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_missing_row()
    {
        var svc = NewSvc();
        (await svc.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    // ── Match: window ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindBlockingFreezeAsync_returns_null_when_no_freeze()
    {
        var svc = NewSvc();
        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid());
        blocking.Should().BeNull();
    }

    [Fact]
    public async Task FindBlockingFreezeAsync_returns_null_before_window_start()
    {
        var svc = NewSvc();
        var input = SampleFreeze();
        input.StartUtc = Now.AddHours(1);
        input.EndUtc   = Now.AddHours(2);
        await svc.CreateAsync(input);

        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid());
        blocking.Should().BeNull("the deployment fires before the freeze starts");
    }

    [Fact]
    public async Task FindBlockingFreezeAsync_returns_null_after_window_end()
    {
        var svc = NewSvc();
        var input = SampleFreeze();
        input.StartUtc = Now.AddHours(-2);
        input.EndUtc   = Now.AddHours(-1); // ends 1 h ago
        await svc.CreateAsync(input);

        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid());
        blocking.Should().BeNull("the window ended before this deployment");
    }

    [Fact]
    public async Task FindBlockingFreezeAsync_returns_freeze_when_window_active()
    {
        var svc = NewSvc();
        var created = await svc.CreateAsync(SampleFreeze());

        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid());

        blocking.Should().NotBeNull();
        blocking!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Disabled_freeze_never_matches()
    {
        // The "draft this lockdown for next month" workflow: freeze on the
        // page but Disabled=true. Must never appear in the match path.
        var svc = NewSvc();
        var input = SampleFreeze();
        input.Disabled = true;
        await svc.CreateAsync(input);

        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid());
        blocking.Should().BeNull();
    }

    // ── Match: project + env scope ─────────────────────────────────────────

    [Fact]
    public async Task Empty_project_list_matches_any_project()
    {
        var svc = NewSvc();
        var input = SampleFreeze();
        input.ProjectIds = []; // explicit "all"
        await svc.CreateAsync(input);

        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid()))
            .Should().NotBeNull("empty list = 'all projects'");
    }

    [Fact]
    public async Task Non_empty_project_list_only_matches_listed_projects()
    {
        var svc = NewSvc();
        var allowedProject = Guid.NewGuid();
        var blockedProject = Guid.NewGuid();

        var input = SampleFreeze();
        input.ProjectIds = [blockedProject];
        await svc.CreateAsync(input);

        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, allowedProject, Guid.NewGuid()))
            .Should().BeNull("the allowed project isn't in the freeze's list");

        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, blockedProject, Guid.NewGuid()))
            .Should().NotBeNull("the blocked project IS in the freeze's list");
    }

    [Fact]
    public async Task Non_empty_environment_list_only_matches_listed_environments()
    {
        var svc = NewSvc();
        var prod = Guid.NewGuid();
        var dev  = Guid.NewGuid();

        var input = SampleFreeze();
        input.EnvironmentIds = [prod];
        await svc.CreateAsync(input);

        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), dev))
            .Should().BeNull("dev is not in the freeze's environment list");

        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), prod))
            .Should().NotBeNull("prod IS in the freeze's environment list");
    }

    [Fact]
    public async Task Combined_scope_requires_all_dimensions_to_match()
    {
        var svc = NewSvc();
        var theProject = Guid.NewGuid();
        var theEnv     = Guid.NewGuid();

        var input = SampleFreeze();
        input.ProjectIds     = [theProject];
        input.EnvironmentIds = [theEnv];
        await svc.CreateAsync(input);

        // Both match → blocked.
        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, theProject, theEnv))
            .Should().NotBeNull();

        // Project matches but env doesn't → not blocked.
        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, theProject, Guid.NewGuid()))
            .Should().BeNull("scope is conjunctive — one mismatch lets the deployment through");

        // Env matches but project doesn't → not blocked.
        (await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), theEnv))
            .Should().BeNull();
    }

    [Fact]
    public async Task Tenant_tag_match_is_case_insensitive()
    {
        // The match is OrdinalIgnoreCase; pinning so a future refactor
        // doesn't tighten it and silently break "Tagsets/customer" vs
        // "tagsets/Customer".
        var svc = NewSvc();
        var input = SampleFreeze();
        input.TenantTagCanonicalNames = ["TagSet/CustomerA"];
        await svc.CreateAsync(input);

        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid(),
            tenantTagCanonicalNames: ["tagset/customera"]);

        blocking.Should().NotBeNull("tag matching is case-insensitive");
    }

    // ── Space isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task Freeze_in_foreign_Space_does_not_block()
    {
        var svc = NewSvc();
        var foreignSpace = Guid.NewGuid();

        // Seed a freeze directly with a foreign SpaceId — bypasses the
        // ambient SpaceContext stamp used by CreateAsync.
        await using (var seed = postgres.CreateContext())
        {
            seed.Spaces.Add(new KrakenDeploy.Server.Core.Domain.Spaces.Space
            {
                Id   = foreignSpace,
                Name = "foreign",
                Slug = $"foreign-{foreignSpace:N}",
            });
            seed.DeploymentFreezes.Add(new DeploymentFreeze
            {
                SpaceId  = foreignSpace,
                Name     = "foreign-freeze",
                StartUtc = Now.AddHours(-1),
                EndUtc   = Now.AddHours(1),
            });
            await seed.SaveChangesAsync();
        }

        var blocking = await svc.FindBlockingFreezeAsync(
            WellKnown.DefaultSpaceId, Guid.NewGuid(), Guid.NewGuid());

        blocking.Should().BeNull(
            "freezes never reach across Spaces — each Space has its own " +
            "release-policy boundary");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private DeploymentFreezeService NewSvc() =>
        new(postgres.ScopeFactory, new FixedClock(Now));

    private static DeploymentFreeze SampleFreeze() => new()
    {
        SpaceId  = WellKnown.DefaultSpaceId,
        Name     = "test-freeze",
        StartUtc = Now.AddHours(-1),
        EndUtc   = Now.AddHours(1),
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

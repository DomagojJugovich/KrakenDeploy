using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// WP5 item 3 — project group rename + delete. Rename works for any group
/// (including the default, whose IsDefault flag is preserved). Delete refuses the
/// bootstrap default group and any group that still holds projects (required
/// RESTRICT FK — members are never orphaned or silently reassigned).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ProjectServiceGroupTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private ProjectService NewService() => new(postgres);

    // ── Rename ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGroupAsync_renames_and_preserves_default_flag()
    {
        var svc = NewService();
        var defaultGroupId = await SeedDefaultGroupAsync();

        var updated = await svc.UpdateGroupAsync(defaultGroupId, "Renamed Default", "renamed-default", "desc");

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Renamed Default");
        updated.Slug.Should().Be("renamed-default");
        updated.IsDefault.Should().BeTrue("renaming must not drop the bootstrap default flag");
    }

    [Fact]
    public async Task UpdateGroupAsync_rejects_a_duplicate_slug()
    {
        var svc = NewService();
        await SeedDefaultGroupAsync();
        var other = await svc.CreateGroupAsync("Other", $"other-{Guid.NewGuid():N}"[..16], null);
        var second = await svc.CreateGroupAsync("Second", $"second-{Guid.NewGuid():N}"[..16], null);

        var act = () => svc.UpdateGroupAsync(second.Id, "Second", other.Slug, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already taken*");
    }

    [Fact]
    public async Task UpdateGroupAsync_returns_null_for_missing_group()
    {
        var svc = NewService();
        (await svc.UpdateGroupAsync(Guid.NewGuid(), "X", "x", null)).Should().BeNull();
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteGroupAsync_removes_an_empty_non_default_group()
    {
        var svc = NewService();
        await SeedDefaultGroupAsync();
        var group = await svc.CreateGroupAsync("Temp", $"temp-{Guid.NewGuid():N}"[..16], null);

        var ok = await svc.DeleteGroupAsync(group.Id);

        ok.Should().BeTrue();
        await using var db = postgres.CreateContext();
        (await db.ProjectGroups.IgnoreQueryFilters().AnyAsync(g => g.Id == group.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task DeleteGroupAsync_refuses_the_default_group()
    {
        var svc = NewService();
        var defaultGroupId = await SeedDefaultGroupAsync();

        var act = () => svc.DeleteGroupAsync(defaultGroupId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Default");
    }

    [Fact]
    public async Task DeleteGroupAsync_refuses_a_group_that_still_holds_projects()
    {
        var svc = NewService();
        await SeedDefaultGroupAsync();
        var group = await svc.CreateGroupAsync("Holding", $"hold-{Guid.NewGuid():N}"[..16], null);
        await SeedProjectInGroupAsync(group.Id);

        var act = () => svc.DeleteGroupAsync(group.Id);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("project");

        await using var db = postgres.CreateContext();
        (await db.ProjectGroups.IgnoreQueryFilters().AnyAsync(g => g.Id == group.Id))
            .Should().BeTrue("a group with members must be preserved");
    }

    [Fact]
    public async Task DeleteGroupAsync_returns_false_for_missing_group()
    {
        var svc = NewService();
        (await svc.DeleteGroupAsync(Guid.NewGuid())).Should().BeFalse();
    }

    // ── Seeding helpers ─────────────────────────────────────────────────────

    private async Task<Guid> SeedDefaultGroupAsync() =>
        await TestData.EnsureProjectGroupAsync(CreateContext(), WellKnown.DefaultSpaceId);

    private KrakenDbContext CreateContext() => postgres.CreateContext();

    private async Task SeedProjectInGroupAsync(Guid groupId)
    {
        await using var db = postgres.CreateContext();
        var slug = $"grpproj-{Guid.NewGuid():N}"[..16];
        db.Projects.Add(new Project
        {
            SpaceId        = WellKnown.DefaultSpaceId,
            Slug           = slug,
            Name           = slug,
            ProjectGroupId = groupId,
        });
        await db.SaveChangesAsync();
    }
}

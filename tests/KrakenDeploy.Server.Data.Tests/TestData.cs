using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Shared seeding helpers for the Postgres integration tests. The
/// <see cref="PostgresFixture"/> container is shared across the collection and
/// persists between tests, so these are all idempotent find-or-create.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Returns the id of the Default Project Group for <paramref name="spaceId"/>,
    /// creating it if absent. Needed because <c>projects.project_group_id</c> is
    /// now a required FK (fix 4 decision 10) — every test-created Project must
    /// point at a real group. Uses <c>IgnoreQueryFilters</c> (ProjectGroup is
    /// space-scoped) and stamps <c>SpaceId</c> explicitly so it works for any
    /// Space, not just the ambient one.
    /// </summary>
    public static async Task<Guid> EnsureProjectGroupAsync(KrakenDbContext db, Guid spaceId)
    {
        var existing = await db.ProjectGroups.IgnoreQueryFilters()
            .Where(g => g.SpaceId == spaceId && g.IsDefault)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Value;
        }

        var group = new ProjectGroup
        {
            SpaceId = spaceId,
            Slug = $"test-default-{spaceId:N}"[..Math.Min(64, $"test-default-{spaceId:N}".Length)],
            Name = "Default Project Group",
            IsDefault = true,
            SortOrder = 0,
        };
        db.ProjectGroups.Add(group);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return group.Id;
    }

    /// <summary>
    /// Ensures an <see cref="ApplicationUser"/> row exists for the given id.
    /// team_members / api_keys now carry a real FK to users (fix 4 decision 1),
    /// so permission tests that used synthetic user GUIDs must first seed the
    /// user. Idempotent against the shared container.
    /// </summary>
    public static async Task EnsureUserAsync(KrakenDbContext db, Guid userId)
    {
        if (await db.Users.AnyAsync(u => u.Id == userId).ConfigureAwait(false))
        {
            return;
        }
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"u{userId:N}",
            NormalizedUserName = $"U{userId:N}",
            Email = $"{userId:N}@example.test",
            NormalizedEmail = $"{userId:N}@EXAMPLE.TEST",
            SecurityStamp = userId.ToString("N"),
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}

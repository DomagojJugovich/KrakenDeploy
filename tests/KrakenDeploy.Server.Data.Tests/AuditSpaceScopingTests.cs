using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Space isolation of interactive audit reads (the 2026-07 leak fix).
/// <para>
/// <c>audit_entries</c> is not <c>ISpaceScoped</c> — no global query filter
/// protects it — so isolation rests entirely on the choke point
/// (<see cref="AuditExportService.ApplySpaceVisibility"/> /
/// <see cref="AuditExportService.ApplyScopedFilter"/>). These tests exercise
/// the exact queries the <c>Audit.razor</c> grid and the per-entity Events
/// tabs (<see cref="AuditLogService.GetForSubjectAsync"/>) issue, against
/// real Postgres, so a regression in EF translation fails too.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AuditSpaceScopingTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid SpaceA = Guid.NewGuid();
    private static readonly Guid SpaceB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AuditEntries.ExecuteDeleteAsync();

        db.AuditEntries.AddRange(
            Entry(SpaceA, "SpaceA.Event",  "DeploymentTarget", "shared-subject", BaseTime),
            Entry(SpaceB, "SpaceB.Event",  "DeploymentTarget", "shared-subject", BaseTime.AddMinutes(1)),
            Entry(null,   "System.Event",  "License",          "shared-subject", BaseTime.AddMinutes(2)));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The page-grid query (Audit.razor → ApplyScopedFilter) ──────────────

    [Fact]
    public async Task Page_query_returns_zero_rows_from_other_spaces()
    {
        await using var db = postgres.CreateContext();
        var rows = await AuditExportService.ApplyScopedFilter(
                db.AuditEntries.AsNoTracking(), PageFilter(SpaceA, includeSystem: false))
            .ToListAsync();

        rows.Should().ContainSingle().Which.EventType.Should().Be("SpaceA.Event",
            "a Space-A operator's grid must contain no Space-B and no system rows");
    }

    [Fact]
    public async Task Page_query_without_system_toggle_hides_null_space_rows()
    {
        await using var db = postgres.CreateContext();
        var rows = await AuditExportService.ApplyScopedFilter(
                db.AuditEntries.AsNoTracking(), PageFilter(SpaceA, includeSystem: false))
            .ToListAsync();

        rows.Should().NotContain(e => e.SpaceId == null,
            "system rows are AdministerSystem-only; the default view excludes them");
    }

    [Fact]
    public async Task Page_query_with_system_toggle_adds_null_space_rows_only()
    {
        // The sysadmin path: IncludeSystemRows=true is only ever set after an
        // AdministerSystem check (page re-validates per load; endpoint 403s).
        await using var db = postgres.CreateContext();
        var rows = await AuditExportService.ApplyScopedFilter(
                db.AuditEntries.AsNoTracking(), PageFilter(SpaceA, includeSystem: true))
            .ToListAsync();

        rows.Select(e => e.EventType).Should().BeEquivalentTo(
            ["SpaceA.Event", "System.Event"],
            "the toggle widens to platform rows — never to another Space's rows");
    }

    [Fact]
    public async Task Unreachable_space_sentinel_returns_nothing()
    {
        // SpaceScopedComponentBase resolves Guid.Empty for a user with no
        // accessible Space at all — the audit grid must fail closed with it.
        await using var db = postgres.CreateContext();
        var rows = await AuditExportService.ApplyScopedFilter(
                db.AuditEntries.AsNoTracking(), PageFilter(Guid.Empty, includeSystem: false))
            .ToListAsync();

        rows.Should().BeEmpty();
    }

    // ── Per-entity history (Events tabs → GetForSubjectAsync) ──────────────

    [Fact]
    public async Task GetForSubjectAsync_is_caged_to_the_given_space()
    {
        // The subject id comes from the page URL: a Space-A user probing a
        // Space-B entity id must get nothing, even for an existing subject.
        var svc = NewAuditLogService();

        var rows = await svc.GetForSubjectAsync(
            "DeploymentTarget", "shared-subject", SpaceA);

        rows.Should().ContainSingle().Which.EventType.Should().Be("SpaceA.Event");
    }

    [Fact]
    public async Task GetForSubjectAsync_excludes_system_rows_and_other_types()
    {
        var svc = NewAuditLogService();

        var rows = await svc.GetForSubjectAsync(
            "License", "shared-subject", SpaceA);

        rows.Should().BeEmpty(
            "the System.Event row has SpaceId == null — platform events are " +
            "not entity history and never appear in a Space-scoped tab");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private AuditLogService NewAuditLogService() => new(
        postgres,
        new HttpContextAccessor(),
        new KrakenDeploy.Server.Data.Spaces.DefaultSpaceContext(),
        TimeProvider.System);

    /// <summary>Mirrors Audit.razor's BuildFilter: active Space + toggle, no
    /// date/text narrowing.</summary>
    private static AuditExportService.Filter PageFilter(Guid spaceId, bool includeSystem) => new(
        SpaceIds:            [spaceId],
        IncludeSystemRows:   includeSystem,
        FromUtc:             null,
        ToUtcExclusive:      null,
        EventTypeContains:   null,
        UserDisplayContains: null,
        SubjectTypeContains: null);

    private static AuditEntry Entry(
        Guid? spaceId, string eventType, string subjectType, string subjectId,
        DateTimeOffset when) => new()
    {
        Id          = Guid.CreateVersion7(),
        SpaceId     = spaceId,
        EventType   = eventType,
        SubjectType = subjectType,
        SubjectId   = subjectId,
        UserDisplay = "u",
        OccurredUtc = when,
    };
}

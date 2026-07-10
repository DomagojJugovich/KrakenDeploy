using System.Text;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="AuditExportService"/>. We use the real
/// Postgres fixture so EF Core's IgnoreQueryFilters / AsAsyncEnumerable
/// pipeline is exercised end-to-end. The CSV-escape edge cases are unit-
/// tested separately in <c>AuditExportServiceCsvEscapeTests</c>; this file
/// pins the end-to-end shape — headers, row order, BOM, JSON structure.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class AuditExportServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The Space every test row lives in unless a test says otherwise —
    /// the Filter's SpaceIds is required, so every export is Space-scoped.</summary>
    private static readonly Guid TestSpaceId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.AuditEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Csv_starts_with_BOM_and_header_then_rows()
    {
        await SeedAsync(
            new AuditEntry
            {
                Id           = Guid.CreateVersion7(),
                SpaceId      = TestSpaceId,
                EventType    = "Project.Created",
                UserDisplay  = "alice@laus.hr",
                OccurredUtc  = BaseTime,
                SubjectType  = "Project",
                SubjectId    = "p-1",
                SubjectName  = "Frontend",
                Details      = "first",
            },
            new AuditEntry
            {
                Id           = Guid.CreateVersion7(),
                SpaceId      = TestSpaceId,
                EventType    = "Project.Updated",
                UserDisplay  = "bob@laus.hr",
                OccurredUtc  = BaseTime.AddMinutes(1),
                SubjectType  = "Project",
                SubjectId    = "p-1",
                SubjectName  = "Frontend",
                Details      = "second",
            });

        var svc = new AuditExportService(postgres);

        using var ms = new MemoryStream();
        await svc.WriteCsvAsync(ms, ScopedFilter, CancellationToken.None);

        var bytes = ms.ToArray();
        // UTF-8 BOM is EF BB BF — Excel relies on it to pick UTF-8.
        bytes.Take(3).Should().Equal((byte)0xEF, (byte)0xBB, (byte)0xBF);

        // The BOM survives decoding as a U+FEFF character at the very start
        // of the string. Strip it before splitting so the header check
        // compares the real header content, not "<BOM>OccurredUtc".
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].TrimEnd('\r').Should().StartWith("OccurredUtc,EventType,UserDisplay,",
            "header must come right after the BOM");

        // Order is DESC-by-OccurredUtc — newer first.
        lines[1].TrimEnd('\r').Should().Contain("Project.Updated")
            .And.Contain("bob@laus.hr");
        lines[2].TrimEnd('\r').Should().Contain("Project.Created");
    }

    [Fact]
    public async Task Csv_escapes_details_with_commas_and_quotes()
    {
        // The Details column is the most likely to contain CSV-hostile
        // content (license summaries, deployment failures with quoted
        // commands). Pin that the escaping survives the full DB round-trip.
        await SeedAsync(new AuditEntry
        {
            Id           = Guid.CreateVersion7(),
            SpaceId      = TestSpaceId,
            EventType    = "License.Uploaded",
            UserDisplay  = "ops",
            OccurredUtc  = BaseTime,
            Details      = "Customer=LAUS d.o.o., Type=Full, MaxTargets=25",
        });

        var svc = new AuditExportService(postgres);
        using var ms = new MemoryStream();
        await svc.WriteCsvAsync(ms, ScopedFilter, CancellationToken.None);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        text.Should().Contain(
            "\"Customer=LAUS d.o.o., Type=Full, MaxTargets=25\"",
            "the commas inside the Details field must be wrapped in quotes " +
            "so they don't break column alignment");
    }

    [Fact]
    public async Task Json_emits_array_of_objects_in_descending_time()
    {
        await SeedAsync(
            new AuditEntry
            {
                Id           = Guid.CreateVersion7(),
                SpaceId      = TestSpaceId,
                EventType    = "First",
                UserDisplay  = "u",
                OccurredUtc  = BaseTime,
            },
            new AuditEntry
            {
                Id           = Guid.CreateVersion7(),
                SpaceId      = TestSpaceId,
                EventType    = "Second",
                UserDisplay  = "u",
                OccurredUtc  = BaseTime.AddSeconds(30),
            });

        var svc = new AuditExportService(postgres);
        using var ms = new MemoryStream();
        await svc.WriteJsonAsync(ms, ScopedFilter, CancellationToken.None);

        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(2);

        // Newer first.
        doc.RootElement[0].GetProperty("eventType").GetString().Should().Be("Second");
        doc.RootElement[1].GetProperty("eventType").GetString().Should().Be("First");
    }

    [Fact]
    public async Task Json_inlines_before_and_after_snapshots_as_raw_objects()
    {
        // BeforeJson / AfterJson are stored as JSON text in the DB. If the
        // export double-stringifies them the consumer can't pull "after.name"
        // without two layers of JSON.Parse — that's a known footgun and
        // worth a regression test.
        await SeedAsync(new AuditEntry
        {
            Id           = Guid.CreateVersion7(),
            SpaceId      = TestSpaceId,
            EventType    = "Project.Modified",
            UserDisplay  = "u",
            OccurredUtc  = BaseTime,
            BeforeJson   = "{\"name\":\"old\"}",
            AfterJson    = "{\"name\":\"new\"}",
        });

        var svc = new AuditExportService(postgres);
        using var ms = new MemoryStream();
        await svc.WriteJsonAsync(ms, ScopedFilter, CancellationToken.None);

        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);

        var row = doc.RootElement[0];
        row.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Object,
            "BeforeJson must surface as a nested object, not a string");
        row.GetProperty("after").GetProperty("name").GetString().Should().Be("new",
            "consumers must be able to access nested fields in one pass");
    }

    [Fact]
    public async Task Filter_applies_event_type_substring()
    {
        await SeedAsync(
            EntryAt(BaseTime,             "Project.Created", "u"),
            EntryAt(BaseTime.AddMinutes(1),"Project.Updated", "u"),
            EntryAt(BaseTime.AddMinutes(2),"User.SignedIn",   "u"));

        var svc = new AuditExportService(postgres);
        var filter = ScopedFilter with { EventTypeContains = "Project" };

        using var ms = new MemoryStream();
        await svc.WriteJsonAsync(ms, filter, CancellationToken.None);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        doc.RootElement.GetArrayLength().Should().Be(2,
            "the User.SignedIn row must be excluded by the EventType filter");
    }

    [Fact]
    public async Task Filter_applies_time_range_inclusively_from_exclusively_to()
    {
        await SeedAsync(
            EntryAt(BaseTime.AddDays(-1), "Old",    "u"),
            EntryAt(BaseTime,             "Inside", "u"),
            EntryAt(BaseTime.AddDays(2),  "Future", "u"));

        var svc = new AuditExportService(postgres);
        var filter = ScopedFilter with
        {
            FromUtc        = BaseTime,
            ToUtcExclusive = BaseTime.AddDays(2), // excludes Future
        };

        using var ms = new MemoryStream();
        await svc.WriteJsonAsync(ms, filter, CancellationToken.None);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("eventType").GetString().Should().Be("Inside",
            "From is inclusive and To is exclusive — match the page's " +
            "BuildQuery() behaviour exactly, otherwise the export would " +
            "show different rows than the screen");
    }

    [Fact]
    public async Task Empty_dataset_produces_valid_empty_outputs()
    {
        var svc = new AuditExportService(postgres);

        using var csvStream = new MemoryStream();
        await svc.WriteCsvAsync(csvStream, ScopedFilter, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(csvStream.ToArray());
        csv.TrimEnd().Should().EndWith("Details",
            "header-only when no rows match — must not throw");

        using var jsonStream = new MemoryStream();
        await svc.WriteJsonAsync(jsonStream, ScopedFilter, CancellationToken.None);
        var json = Encoding.UTF8.GetString(jsonStream.ToArray());
        json.Should().Be("[]",
            "empty array is a valid JSON document; consumers can parse it");
    }

    [Fact]
    public async Task Export_excludes_other_space_and_system_rows()
    {
        // The 2026-07 audit-leak fix: exports are caged to the Filter's
        // SpaceIds. A Space-A operator must see neither Space B's rows nor
        // NULL-Space (platform) rows — audit entries carry full before/after
        // entity snapshots, so an unscoped export is a cross-tenant leak.
        // Exercise BOTH formats: they build their queries independently.
        var spaceB = Guid.NewGuid();

        await SeedAsync(
            new AuditEntry
            {
                Id          = Guid.CreateVersion7(),
                EventType   = "Mine",
                UserDisplay = "u",
                OccurredUtc = BaseTime,
                SpaceId     = TestSpaceId,
            },
            new AuditEntry
            {
                Id          = Guid.CreateVersion7(),
                EventType   = "OtherSpace",
                UserDisplay = "u",
                OccurredUtc = BaseTime.AddMinutes(1),
                SpaceId     = spaceB,
            },
            new AuditEntry
            {
                Id          = Guid.CreateVersion7(),
                EventType   = "SystemEvent",
                UserDisplay = "System",
                OccurredUtc = BaseTime.AddMinutes(2),
                SpaceId     = null,
            });

        var svc = new AuditExportService(postgres);

        using var jsonStream = new MemoryStream();
        await svc.WriteJsonAsync(jsonStream, ScopedFilter, CancellationToken.None);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(jsonStream.ToArray()));
        doc.RootElement.GetArrayLength().Should().Be(1,
            "only the caller's own Space row may appear in the JSON export");
        doc.RootElement[0].GetProperty("eventType").GetString().Should().Be("Mine");

        using var csvStream = new MemoryStream();
        await svc.WriteCsvAsync(csvStream, ScopedFilter, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(csvStream.ToArray());
        csv.Should().Contain("Mine");
        csv.Should().NotContain("OtherSpace",
            "the CSV export must not leak another Space's rows");
        csv.Should().NotContain("SystemEvent",
            "NULL-Space rows require IncludeSystemRows (sysadmin only)");
    }

    [Fact]
    public async Task IncludeSystemRows_adds_null_space_rows_but_not_other_spaces()
    {
        // The sysadmin "include system events" path: IncludeSystemRows widens
        // the export to platform rows (SpaceId == null) — and ONLY those.
        // Another Space's rows stay invisible even to this wider filter.
        var spaceB = Guid.NewGuid();

        await SeedAsync(
            EntryAt(BaseTime,               "Mine",        "u"),
            new AuditEntry
            {
                Id          = Guid.CreateVersion7(),
                EventType   = "OtherSpace",
                UserDisplay = "u",
                OccurredUtc = BaseTime.AddMinutes(1),
                SpaceId     = spaceB,
            },
            new AuditEntry
            {
                Id          = Guid.CreateVersion7(),
                EventType   = "SystemEvent",
                UserDisplay = "System",
                OccurredUtc = BaseTime.AddMinutes(2),
                SpaceId     = null,
            });

        var svc = new AuditExportService(postgres);
        using var ms = new MemoryStream();
        await svc.WriteJsonAsync(ms, ScopedFilter with { IncludeSystemRows = true },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        var types = Enumerable.Range(0, doc.RootElement.GetArrayLength())
            .Select(i => doc.RootElement[i].GetProperty("eventType").GetString())
            .ToList();
        types.Should().BeEquivalentTo(["SystemEvent", "Mine"],
            "IncludeSystemRows adds NULL-Space rows only — never other Spaces'");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static readonly AuditExportService.Filter ScopedFilter = new(
        SpaceIds:            [TestSpaceId],
        IncludeSystemRows:   false,
        FromUtc:             null,
        ToUtcExclusive:      null,
        EventTypeContains:   null,
        UserDisplayContains: null,
        SubjectTypeContains: null);

    private static AuditEntry EntryAt(DateTimeOffset when, string eventType, string user) =>
        new()
        {
            Id          = Guid.CreateVersion7(),
            SpaceId     = TestSpaceId,
            EventType   = eventType,
            UserDisplay = user,
            OccurredUtc = when,
        };

    private async Task SeedAsync(params AuditEntry[] entries)
    {
        await using var db = postgres.CreateContext();
        db.AuditEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }
}

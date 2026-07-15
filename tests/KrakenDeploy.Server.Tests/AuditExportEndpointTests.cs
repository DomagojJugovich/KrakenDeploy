using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Services;
using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Parameter-binding and authorization contract of
/// <see cref="AuditExportEndpoint.ResolveFilterAsync"/> — the resolver behind
/// <c>/api/audit/export.csv|.json</c>. This is the layer that turned the
/// 2026-07 audit leak into a 403: the endpoint's coarse policy gate cannot
/// evaluate the requested Space (the /api surface has no ambient Space), so
/// every Space decision the streamed filter encodes is made — and must be
/// tested — here.
/// </summary>
public sealed class AuditExportEndpointTests
{
    private static readonly Guid SpaceA = Guid.NewGuid();
    private static readonly Guid SpaceB = Guid.NewGuid();

    // ── space parameter binding ─────────────────────────────────────────────

    [Fact]
    public async Task Missing_space_parameter_is_a_400()
    {
        var (filter, error) = await Resolve("", Evaluator(accessible: [SpaceA]));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status400BadRequest,
            "the Space decision is mandatory — no parameter must never mean 'all Spaces'");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("123")]
    [InlineData("' OR 1=1 --")]
    public async Task Malformed_space_parameter_is_a_400(string raw)
    {
        var (filter, error) = await Resolve($"space={Uri.EscapeDataString(raw)}",
            Evaluator(accessible: [SpaceA]));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── tenant boundary ────────────────────────────────────────────────────

    [Fact]
    public async Task Space_outside_accessible_set_is_a_403()
    {
        // Caller is a member of Space A only, asks for Space B's audit log.
        var (filter, error) = await Resolve($"space={SpaceB}",
            Evaluator(accessible: [SpaceA], eventViewSpaces: [SpaceA, SpaceB]));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status403Forbidden,
            "GetAccessibleSpaceIdsAsync is the hard tenant boundary — a " +
            "permission grant alone must not open another Space's audit log");
    }

    [Fact]
    public async Task Accessible_space_without_EventView_is_a_403()
    {
        var (filter, error) = await Resolve($"space={SpaceA}",
            Evaluator(accessible: [SpaceA], eventViewSpaces: []));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status403Forbidden,
            "membership grants reach, not audit-read rights — EventView must " +
            "hold in the REQUESTED Space");
    }

    // ── system rows (SpaceId == null) ──────────────────────────────────────

    [Fact]
    public async Task IncludeSystem_without_AdministerSystem_is_a_403()
    {
        var (filter, error) = await Resolve(
            $"space={SpaceA}&includeSystem=true",
            Evaluator(accessible: [SpaceA], eventViewSpaces: [SpaceA], isSysAdmin: false));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status403Forbidden,
            "failing loud beats silently dropping rows the caller asked for");
    }

    [Fact]
    public async Task IncludeSystem_with_AdministerSystem_widens_the_filter()
    {
        var (filter, error) = await Resolve(
            $"space={SpaceA}&includeSystem=true",
            Evaluator(accessible: [SpaceA], eventViewSpaces: [SpaceA], isSysAdmin: true));

        error.Should().BeNull();
        filter!.IncludeSystemRows.Should().BeTrue();
        filter.SpaceIds.Should().BeEquivalentTo([SpaceA]);
    }

    [Fact]
    public async Task Malformed_includeSystem_is_a_400()
    {
        var (filter, error) = await Resolve(
            $"space={SpaceA}&includeSystem=yes-please",
            Evaluator(accessible: [SpaceA], eventViewSpaces: [SpaceA], isSysAdmin: true));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── remaining filter binding ───────────────────────────────────────────

    [Fact]
    public async Task Happy_path_binds_all_filter_dimensions()
    {
        var (filter, error) = await Resolve(
            $"space={SpaceA}"
            + "&from=2026-05-01T00:00:00.0000000%2B00:00"
            + "&to=2026-06-01T00:00:00.0000000%2B00:00"
            + "&eventType=Project&user=alice&subjectType=Deployment",
            Evaluator(accessible: [SpaceA], eventViewSpaces: [SpaceA]));

        error.Should().BeNull();
        filter!.SpaceIds.Should().BeEquivalentTo([SpaceA]);
        filter.IncludeSystemRows.Should().BeFalse("absent flag must default to the narrow view");
        filter.FromUtc.Should().Be(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        filter.ToUtcExclusive.Should().Be(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        filter.EventTypeContains.Should().Be("Project");
        filter.UserDisplayContains.Should().Be("alice");
        filter.SubjectTypeContains.Should().Be("Deployment");
    }

    [Theory]
    [InlineData("from=yesterday")]
    [InlineData("to=2026-13-45")]
    public async Task Malformed_dates_are_a_400_not_a_widened_window(string datePart)
    {
        // DateTimeOffset.TryParse failing must not silently null the boundary:
        // a null boundary WIDENS the window beyond what the caller asked for.
        var (filter, error) = await Resolve($"space={SpaceA}&{datePart}",
            Evaluator(accessible: [SpaceA], eventViewSpaces: [SpaceA]));

        filter.Should().BeNull();
        StatusOf(error).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Harness ────────────────────────────────────────────────────────────

    private static async Task<(Data.Services.AuditExportService.Filter? Filter, IResult? Error)>
        Resolve(string queryString, IPermissionEvaluator evaluator)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = queryString.Length == 0
            ? QueryString.Empty
            : new QueryString("?" + queryString);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        return await AuditExportEndpoint.ResolveFilterAsync(
            ctx.Request, user, evaluator, CancellationToken.None);
    }

    private static FakePermissionEvaluator Evaluator(
        Guid[] accessible,
        Guid[]? eventViewSpaces = null,
        bool isSysAdmin = false) =>
        new(accessible.ToHashSet(), (eventViewSpaces ?? accessible).ToHashSet(), isSysAdmin);

    private static int StatusOf(IResult? result)
    {
        result.Should().NotBeNull();
        // Results.StatusCode / Results.BadRequest both surface the code via
        // IStatusCodeHttpResult — no need to execute the result pipeline.
        return ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result!)
            .StatusCode!.Value;
    }

    /// <summary>
    /// Deterministic evaluator: membership set, EventView-holding Spaces, and
    /// the sysadmin bit are injected per test. AdministerSystem does NOT imply
    /// the other answers here — each check is pinned independently so a test
    /// failure names the exact rule that regressed.
    /// </summary>
    private sealed class FakePermissionEvaluator(
        IReadOnlySet<Guid> accessible,
        IReadOnlySet<Guid> eventViewSpaces,
        bool isSysAdmin) : IPermissionEvaluator
    {
        public Task<bool> HasPermissionAsync(
            ClaimsPrincipal user, Permission permission, PermissionScope scope = default,
            bool bypassCache = false, bool strictScope = false, CancellationToken ct = default) => Task.FromResult(
            permission switch
            {
                Permission.AdministerSystem => isSysAdmin,
                Permission.EventView        => scope.SpaceId is { } s && eventViewSpaces.Contains(s),
                _                           => false,
            });

        public Task<IReadOnlySet<Permission>> GetPermissionsAsync(
            ClaimsPrincipal user, PermissionScope scope = default, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

        public Task<IReadOnlySet<Guid>> GetAccessibleSpaceIdsAsync(
            ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(accessible);
    }
}

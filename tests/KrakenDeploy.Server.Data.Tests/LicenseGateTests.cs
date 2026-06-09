using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for M13.E.3 — the license-quota gate inside
/// <see cref="TargetRegistrationService"/>. We use the postgres fixture so
/// the IgnoreQueryFilters count actually exercises against the global query
/// filter (otherwise foreign-Space rows would silently fall out of the count
/// and the gate would let an operator drift past the cap).
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class LicenseGateTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        // Each test starts with a clean target table — counts must be
        // deterministic for the cap arithmetic.
        await db.DeploymentTargets.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateAsync_succeeds_when_gate_allows()
    {
        var svc = new TargetRegistrationService(
            postgres, TimeProvider.System, FakeLicenseGate.Unlimited);

        var (target, token) = await svc.CreateAsync(
            "under-cap", ["web"], TransportMode.Reverse);

        target.Should().NotBeNull();
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateAsync_throws_LicenseLimitException_when_gate_refuses()
    {
        // Gate is hard-coded to refuse. The exception message is the gate's
        // verbatim refusal string — that's the contract the UI relies on to
        // render a user-friendly error instead of the generic toast.
        var refusing = new FakeLicenseGate(targetRefusal: "Target limit reached (10/10). Upgrade your license.");
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, refusing);

        var act = async () => await svc.CreateAsync(
            "would-go-over-cap", ["web"], TransportMode.Reverse);

        var ex = await act.Should().ThrowAsync<LicenseLimitException>();
        ex.Which.Message.Should().Be("Target limit reached (10/10). Upgrade your license.");
    }

    [Fact]
    public async Task CreateAsync_does_not_insert_row_when_gate_refuses()
    {
        // Belt-and-braces: confirm we abort BEFORE the DB INSERT. Without
        // this, a partially-inserted row would still tick the cap counter
        // for the next attempt.
        var refusing = new FakeLicenseGate(targetRefusal: "no");
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, refusing);

        try { await svc.CreateAsync("rejected", ["web"], TransportMode.Reverse); }
        catch (LicenseLimitException) { /* expected */ }

        await using var db = postgres.CreateContext();
        var count = await db.DeploymentTargets.IgnoreQueryFilters().CountAsync();
        count.Should().Be(0, "the gate refused before the SaveChanges fired");
    }

    [Fact]
    public async Task CreateAsync_passes_global_target_count_to_gate()
    {
        // Pin the contract that the count is server-wide (IgnoreQueryFilters),
        // not Space-scoped. Seed a Space-B target via IgnoreQueryFilters so
        // the SpaceScopingInterceptor doesn't redirect it back to the ambient
        // (Default) Space, then verify the gate sees count == 1 on the next
        // create under Space-A. If the data service forgot IgnoreQueryFilters
        // on its count query the gate would be told 0 and a multi-Space
        // operator could drift past the cap by hopping Spaces.
        var foreignSpaceId = Guid.NewGuid();
        await using (var seed = postgres.CreateContext())
        {
            // INSERTs are not affected by query filters, only reads. We
            // create the Space row first so the DeploymentTarget FK has
            // something to point at. SpaceScopingInterceptor preserves the
            // caller-set SpaceId on the target.
            seed.Spaces.Add(new Space
            {
                Id    = foreignSpaceId,
                Name  = "foreign-space",
                Slug  = $"foreign-{foreignSpaceId:N}",
            });
            seed.DeploymentTargets.Add(new DeploymentTarget
            {
                Name          = "foreign-space-target",
                Roles         = ["web"],
                TransportMode = TransportMode.Reverse,
                Status        = TargetStatus.Unknown,
                SpaceId       = foreignSpaceId,
            });
            await seed.SaveChangesAsync();
        }

        var observed = new CountObservingGate();
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, observed);

        await svc.CreateAsync("ambient-space", ["web"], TransportMode.Reverse);

        observed.LastObservedCount.Should().Be(1,
            "the gate must see foreign-Space rows — otherwise a multi-Space " +
            "operator could exceed the server-wide cap by hopping Spaces");
    }

    // ── Test helpers ───────────────────────────────────────────────────────

    private sealed class CountObservingGate : ILicenseGate
    {
        public int LastObservedCount { get; private set; } = -1;

        public string? CheckTargetCreate(int currentTargetCount)
        {
            LastObservedCount = currentTargetCount;
            return null; // always allow
        }

        public string? CheckUserCreate(int currentUserCount) => null;
    }
}

using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="ApiKeyService"/> (M13.C.4): the
/// shown-once token contract, hash-only persistence, revocation, the
/// auth-time lookup, and the last-used throttle gate.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class ApiKeyServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<(string EventType, string? Details)> _auditRows = [];

    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.ApiKeys.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Creation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_returns_plaintext_once_and_persists_only_the_hash()
    {
        var user = await SeedUserAsync("alice");
        var svc = BuildService();

        var created = await svc.CreateAsync(user.Id, "CI pipeline");

        created.PlainToken.Should().StartWith("kd-",
            "the token format is kd-{prefix}-{secret} per the M13.C.4 design");
        created.PlainToken.Should().StartWith(created.Key.Prefix,
            "the stored display prefix must let an operator match a configured " +
            "token to its grid row");

        await using var db = postgres.CreateContext();
        var row = await db.ApiKeys.SingleAsync();
        row.KeyHash.Should().Be(ApiKeyService.Hash(created.PlainToken),
            "only the SHA-256 of the full token may be persisted");
        row.KeyHash.Should().NotContain(created.PlainToken[^10..],
            "no fragment of the secret may appear in the stored row");
        row.Scope.Should().Be(ApiKeyScope.Full);
        row.RevokedUtc.Should().BeNull();

        _auditRows.Should().ContainSingle(r => r.EventType == AuditEventType.ApiKeyCreated)
            .Which.Details.Should().NotContain(created.PlainToken[^10..],
                "the audit trail must never carry the secret");
    }

    [Fact]
    public async Task Create_rejects_duplicate_name_for_the_same_owner()
    {
        var user = await SeedUserAsync("bob");
        var svc = BuildService();
        await svc.CreateAsync(user.Id, "CI");

        var act = () => svc.CreateAsync(user.Id, "CI");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has an API key named*");
    }

    [Fact]
    public async Task Create_allows_same_name_for_different_owners()
    {
        var a = await SeedUserAsync("carol");
        var b = await SeedUserAsync("dave");
        var svc = BuildService();

        await svc.CreateAsync(a.Id, "CI");
        var act = () => svc.CreateAsync(b.Id, "CI");

        await act.Should().NotThrowAsync("uniqueness is per owner, not global");
    }

    [Fact]
    public async Task Create_concurrent_duplicate_names_surface_the_friendly_error_not_DbUpdateException()
    {
        // TOCTOU past the AnyAsync pre-check: two racing creates for the same
        // (owner, name). The unique index is the backstop; the loser must
        // surface InvalidOperationException (what the dialog + CLI catch), not
        // a raw DbUpdateException (unhandled → broken circuit).
        var user = await SeedUserAsync("mallory");
        var svc = BuildService();

        var t1 = svc.CreateAsync(user.Id, "race");
        var t2 = svc.CreateAsync(user.Id, "race");

        var results = await Task.WhenAll(
            Wrap(t1), Wrap(t2));

        results.Count(r => r.ok).Should().Be(1, "exactly one create wins");
        var loser = results.Single(r => !r.ok);
        loser.ex.Should().BeOfType<InvalidOperationException>(
            "the unique-index loser must be translated, never a raw DbUpdateException");
        loser.ex!.Message.Should().Contain("already has an API key named");

        await using var db = postgres.CreateContext();
        (await db.ApiKeys.CountAsync(k => k.UserId == user.Id && k.Name == "race"))
            .Should().Be(1);

        static async Task<(bool ok, Exception? ex)> Wrap(Task<CreatedApiKey> t)
        {
            try { await t; return (true, null); }
            catch (Exception e) { return (false, e); }
        }
    }

    [Fact]
    public async Task Create_for_another_principal_is_allowed_only_for_service_accounts()
    {
        var human = await SeedUserAsync("nathan");
        var service = await SeedUserAsync("svc-bot", UserKind.ServiceAccount);
        var admin = await SeedUserAsync("admin");
        var svc = BuildService();

        // Admin minting for a HUMAN (caller != owner, owner is Human) → rejected
        // at the service boundary (non-repudiation), regardless of any UI flag.
        await ((Func<Task>)(() => svc.CreateAsync(human.Id, "k", mintingCallerId: admin.Id)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*own account or for a service account*");

        // Admin minting for a SERVICE ACCOUNT → allowed.
        await ((Func<Task>)(() => svc.CreateAsync(service.Id, "k", mintingCallerId: admin.Id)))
            .Should().NotThrowAsync();

        // Self-mint (caller == owner) → allowed even for a human.
        await ((Func<Task>)(() => svc.CreateAsync(human.Id, "self", mintingCallerId: human.Id)))
            .Should().NotThrowAsync();

        // Trusted operator context (CLI, mintingCallerId null) → unrestricted.
        await ((Func<Task>)(() => svc.CreateAsync(human.Id, "cli", mintingCallerId: null)))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task Create_rejects_unknown_user_and_past_expiry_and_unknown_space()
    {
        var user = await SeedUserAsync("erin");
        var svc = BuildService();

        await ((Func<Task>)(() => svc.CreateAsync(Guid.NewGuid(), "x")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*No user*");

        await ((Func<Task>)(() => svc.CreateAsync(
                user.Id, "x", expiresUtc: DateTimeOffset.UtcNow.AddDays(-1))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*future*");

        await ((Func<Task>)(() => svc.CreateAsync(
                user.Id, "x", spaceId: Guid.NewGuid())))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*No Space*");
    }

    [Fact]
    public async Task Create_accepts_a_real_space_restriction()
    {
        var user = await SeedUserAsync("frank");
        Guid spaceId;
        await using (var db = postgres.CreateContext())
        {
            var space = new Space { Slug = $"s-{Guid.NewGuid():N}", Name = "Restricted" };
            db.Spaces.Add(space);
            await db.SaveChangesAsync();
            spaceId = space.Id;
        }

        var created = await BuildService().CreateAsync(user.Id, "scoped", spaceId: spaceId);

        created.Key.SpaceId.Should().Be(spaceId);
    }

    // ── Auth-time lookup ────────────────────────────────────────────────────

    [Fact]
    public async Task FindByToken_roundtrips_the_created_token_and_rejects_others()
    {
        var user = await SeedUserAsync("grace");
        var svc = BuildService();
        var created = await svc.CreateAsync(user.Id, "lookup");

        var found = await svc.FindByTokenAsync(created.PlainToken);
        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Key.Id);

        (await svc.FindByTokenAsync("kd-DEADBEEF-not-a-real-secret")).Should().BeNull();
        (await svc.FindByTokenAsync("")).Should().BeNull();
        (await svc.FindByTokenAsync(created.PlainToken + "x")).Should().BeNull(
            "the hash covers the FULL token — any mutation must miss");
    }

    [Fact]
    public async Task FindByToken_returns_revoked_and_expired_rows_for_precise_logging()
    {
        var user = await SeedUserAsync("heidi");
        var svc = BuildService();
        var created = await svc.CreateAsync(user.Id, "will-revoke");
        await svc.RevokeAsync(created.Key.Id);

        var found = await svc.FindByTokenAsync(created.PlainToken);

        found.Should().NotBeNull("the handler distinguishes revoked from unknown");
        found!.RevokedUtc.Should().NotBeNull();
        found.IsActive(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    // ── Revocation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_is_idempotent_and_audited()
    {
        var user = await SeedUserAsync("ivan");
        var svc = BuildService();
        var created = await svc.CreateAsync(user.Id, "revoke-me");

        (await svc.RevokeAsync(created.Key.Id)).Should().BeTrue();
        await using (var db = postgres.CreateContext())
        {
            var stamp = (await db.ApiKeys.SingleAsync()).RevokedUtc;
            stamp.Should().NotBeNull();

            (await svc.RevokeAsync(created.Key.Id)).Should().BeTrue();
            var second = (await db.ApiKeys.AsNoTracking().SingleAsync()).RevokedUtc;
            second.Should().Be(stamp, "re-revoking must keep the first timestamp");
        }

        (await svc.RevokeAsync(Guid.NewGuid())).Should().BeFalse();
        _auditRows.Count(r => r.EventType == AuditEventType.ApiKeyRevoked).Should().Be(1,
            "the no-op second revoke must not write a second audit row");
    }

    // ── Last-used ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TouchLastUsed_writes_the_timestamp()
    {
        var user = await SeedUserAsync("judy");
        var svc = BuildService();
        var created = await svc.CreateAsync(user.Id, "touch");

        await svc.TouchLastUsedAsync(created.Key.Id);

        await using var db = postgres.CreateContext();
        (await db.ApiKeys.SingleAsync()).LastUsedUtc.Should().NotBeNull();
    }

    [Fact]
    public void UsageTracker_gates_to_one_write_per_threshold_window()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero));
        var tracker = new ApiKeyUsageTracker(time);
        var key = Guid.NewGuid();

        tracker.ShouldWrite(key).Should().BeTrue("first sighting is always due");
        tracker.ShouldWrite(key).Should().BeFalse("immediately after, the slot is claimed");

        time.Advance(ApiKeyUsageTracker.Threshold);
        tracker.ShouldWrite(key).Should().BeTrue("a full threshold later the write is due again");

        tracker.ShouldWrite(Guid.NewGuid()).Should().BeTrue("keys are tracked independently");
    }

    // ── Full auth decision (drives the auth handler) ────────────────────────

    [Fact]
    public async Task AuthenticateToken_distinguishes_every_failure_mode()
    {
        var user = await SeedUserAsync("kate");
        var time = new FakeTime(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
        var svc = BuildService(time);

        // Unknown / blank.
        (await svc.AuthenticateTokenAsync("kd-00000000-nope")).Status
            .Should().Be(ApiKeyAuthStatus.UnknownKey);
        (await svc.AuthenticateTokenAsync("")).Status
            .Should().Be(ApiKeyAuthStatus.UnknownKey);

        // Active — owner name resolved for the principal's Name claim.
        var live = await svc.CreateAsync(user.Id, "live");
        var active = await svc.AuthenticateTokenAsync(live.PlainToken);
        active.Status.Should().Be(ApiKeyAuthStatus.Active);
        active.OwnerUserName.Should().Be("kate");
        active.Key!.Id.Should().Be(live.Key.Id);

        // Revoked.
        await svc.RevokeAsync(live.Key.Id);
        (await svc.AuthenticateTokenAsync(live.PlainToken)).Status
            .Should().Be(ApiKeyAuthStatus.Revoked);

        // Expired — valid at mint time, then the clock passes the expiry.
        var expiring = await svc.CreateAsync(user.Id, "expiring",
            expiresUtc: time.GetUtcNow().AddHours(1));
        time.Advance(TimeSpan.FromHours(2));
        (await svc.AuthenticateTokenAsync(expiring.PlainToken)).Status
            .Should().Be(ApiKeyAuthStatus.Expired);
    }

    [Fact]
    public async Task AuthenticateToken_fails_closed_when_the_owner_row_is_gone()
    {
        var user = await SeedUserAsync("leo");
        var svc = BuildService();
        var created = await svc.CreateAsync(user.Id, "orphan");

        // Delete the user OUT-OF-BAND (bypassing UserService.DeleteAsync's key
        // cleanup) to simulate the should-never-happen orphan row.
        await using (var db = postgres.CreateContext())
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }

        (await svc.AuthenticateTokenAsync(created.PlainToken)).Status
            .Should().Be(ApiKeyAuthStatus.OwnerMissing,
                "a key must never authenticate a principal that no longer exists");
    }

    [Fact]
    public void GenerateToken_produces_unique_high_entropy_tokens()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var (plain, prefix, hash) = ApiKeyService.GenerateToken();
            plain.Should().MatchRegex("^kd-[0-9A-F]{8}-[A-Za-z0-9_-]{43}$");
            prefix.Should().Be(plain[..11]);
            hash.Should().MatchRegex("^[0-9a-f]{64}$");
            seen.Add(plain).Should().BeTrue("collisions would be catastrophic");
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private ApiKeyService BuildService(TimeProvider? time = null) =>
        new(postgres, time ?? TimeProvider.System, new ListAuditLog(_auditRows));

    private async Task<ApplicationUser> SeedUserAsync(string name, UserKind kind = UserKind.Human)
    {
        await using var db = postgres.CreateContext();
        var user = new ApplicationUser
        {
            UserName           = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email              = $"{name}@test.local",
            NormalizedEmail    = $"{name}@TEST.LOCAL".ToUpperInvariant(),
            EmailConfirmed     = true,
            Kind               = kind,
            SecurityStamp      = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private sealed class ListAuditLog(List<(string, string?)> rows) : IAuditLog
    {
        public Task RecordAsync(
            string eventType,
            string? subjectType = null,
            string? subjectId = null,
            string? subjectName = null,
            string? details = null,
            Guid? userId = null,
            string? userDisplay = null,
            CancellationToken ct = default)
        {
            rows.Add((eventType, details));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

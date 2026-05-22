using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

[Collection("Postgres")]
public class TargetRegistrationServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Create_returns_plain_token_and_stores_only_hash()
    {
        await using var db = postgres.CreateContext();
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, FakeLicenseGate.Unlimited);

        var (target, token) = await svc.CreateAsync("smoke", ["web"], TransportMode.Reverse);

        token.Should().NotBeNullOrEmpty();
        target.RegistrationKeyHash.Should().NotBeNullOrEmpty();
        target.RegistrationKeyHash.Should().NotBe(token,
            because: "only the SHA-256 hash should be stored, not the raw token");
        target.RegistrationTokenExpiresUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ValidateAndConsume_returns_target_and_nulls_out_hash_on_first_use()
    {
        await using var db = postgres.CreateContext();
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, FakeLicenseGate.Unlimited);

        var (_, token) = await svc.CreateAsync("smoke-validate", ["web"], TransportMode.Reverse);
        var result = await svc.ValidateAndConsumeTokenAsync(token);

        result.Should().NotBeNull();
        result!.RegistrationKeyHash.Should().BeNull(because: "token is consumed on first use");
        result.RegistrationTokenExpiresUtc.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAndConsume_returns_null_for_unknown_token()
    {
        await using var db = postgres.CreateContext();
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, FakeLicenseGate.Unlimited);

        var result = await svc.ValidateAndConsumeTokenAsync("totally-made-up-token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAndConsume_returns_null_on_second_use()
    {
        await using var db = postgres.CreateContext();
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, FakeLicenseGate.Unlimited);

        var (_, token) = await svc.CreateAsync("smoke-double", ["web"], TransportMode.Reverse);

        await svc.ValidateAndConsumeTokenAsync(token); // first use — consumes it
        var second = await svc.ValidateAndConsumeTokenAsync(token); // second use

        second.Should().BeNull(because: "the token was already consumed");
    }

    [Fact]
    public async Task ValidateAndConsume_returns_null_for_expired_token()
    {
        // Create with real clock so expiry is ~24 h from now.
        var createSvc = new TargetRegistrationService(postgres, TimeProvider.System, FakeLicenseGate.Unlimited);
        var (_, token) = await createSvc.CreateAsync("smoke-expired", ["web"], TransportMode.Reverse);

        // Validate 25 hours later — must be rejected (lifetime is 24 h).
        var futureSvc = new TargetRegistrationService(postgres,
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(25)),
            FakeLicenseGate.Unlimited);

        var result = await futureSvc.ValidateAndConsumeTokenAsync(token);

        result.Should().BeNull(because: "the token expired 1 h before this validation attempt");
    }

    [Fact]
    public async Task RotateToken_replaces_existing_token()
    {
        await using var db = postgres.CreateContext();
        var svc = new TargetRegistrationService(postgres, TimeProvider.System, FakeLicenseGate.Unlimited);

        var (target, originalToken) = await svc.CreateAsync("smoke-rotate", ["web"], TransportMode.Reverse);

        var newToken = await svc.RotateTokenAsync(target.Id);

        newToken.Should().NotBe(originalToken, because: "a new random token must be generated");

        // Original token must no longer validate.
        var withOld = await svc.ValidateAndConsumeTokenAsync(originalToken);
        withOld.Should().BeNull(because: "the original token was rotated away");

        // New token must validate.
        var withNew = await svc.ValidateAndConsumeTokenAsync(newToken);
        withNew.Should().NotBeNull();
    }
}

// ── Test helpers ────────────────────────────────────────────────────────────

file sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Services;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the pure quota-evaluation core of <c>LicenseService</c>.
/// The instance methods <c>CheckTargetCreate</c> / <c>CheckUserCreate</c> are
/// thin wrappers around these statics; tests here pin the arithmetic +
/// edge-case behaviour without spinning up the JWT validator or touching
/// the real clock.
/// </summary>
public sealed class LicenseServiceQuotaTests
{
    // ── Health gate ────────────────────────────────────────────────────────

    [Fact]
    public void Null_result_refuses_with_no_license_message()
    {
        var refusal = LicenseService.EvaluateLicenseHealth(null, DateTimeOffset.UtcNow);

        refusal.Should().NotBeNull()
            .And.Contain("No valid license")
            .And.Contain("Settings → License",
                "the message must steer the operator to the upload page; " +
                "vague errors generate support tickets");
    }

    [Fact]
    public void Invalid_result_refuses_with_no_license_message()
    {
        // E.g. file present but JWT signature didn't verify.
        var invalid = new LicenseValidationResult(
            IsValid: false, Claims: null, ErrorMessage: "signature mismatch");

        var refusal = LicenseService.EvaluateLicenseHealth(invalid, DateTimeOffset.UtcNow);

        refusal.Should().NotBeNull().And.Contain("No valid license");
    }

    [Fact]
    public void Expired_license_refuses_with_expired_message()
    {
        // ExpiresUtc is 1 hour in the past relative to "now".
        var now = new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);
        var expired = ResultWith(
            maxTargets: 100, maxUsers: 100,
            expiresUtc: now.AddHours(-1));

        var refusal = LicenseService.EvaluateLicenseHealth(expired, now);

        refusal.Should().Be(
            "License has expired. Upload a new license key to add more resources.");
    }

    [Fact]
    public void Healthy_unexpired_license_passes_health_gate()
    {
        var now = new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);
        var ok = ResultWith(
            maxTargets: 10, maxUsers: 5,
            expiresUtc: now.AddDays(30));

        LicenseService.EvaluateLicenseHealth(ok, now).Should().BeNull();
    }

    // ── Target cap ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]   // empty server
    [InlineData(5)]   // mid-range
    [InlineData(9)]   // one under cap
    public void Under_target_cap_allows_create(int currentCount)
    {
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxTargets: 10, expiresUtc: now.AddDays(30));

        LicenseService.EvaluateTargetCreate(result, currentCount, now).Should().BeNull();
    }

    [Theory]
    [InlineData(10)]  // exactly at cap — refuse (one more would be over)
    [InlineData(11)]  // already over (data corruption / cap was lowered) — still refuse
    [InlineData(50)]  // way over — refuse
    public void At_or_above_target_cap_refuses_create(int currentCount)
    {
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxTargets: 10, expiresUtc: now.AddDays(30));

        var refusal = LicenseService.EvaluateTargetCreate(result, currentCount, now);

        refusal.Should().NotBeNull()
            .And.StartWith("Target limit reached")
            .And.Contain($"({currentCount}/10)",
                "the operator needs to see how far over they are");
    }

    [Fact]
    public void Zero_target_cap_means_unlimited()
    {
        // A license with max_targets=0 is the developer/internal license —
        // we want it to act as "no cap", same convention as the Retention=0
        // disables-purging contract in M13.F.4. Otherwise a fresh dev
        // license would block the very first target.
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxTargets: 0, expiresUtc: now.AddDays(30));

        LicenseService.EvaluateTargetCreate(result, currentTargetCount: 1_000_000, now)
            .Should().BeNull(
                "MaxTargets == 0 is treated as 'unlimited' — same as " +
                "retention 0 disabling the purge");
    }

    [Fact]
    public void Negative_target_count_throws()
    {
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxTargets: 10, expiresUtc: now.AddDays(30));

        var act = () => LicenseService.EvaluateTargetCreate(result, -1, now);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "counts come from DB COUNT() — negative is a programmer bug, " +
            "not a runtime condition we should hide behind a refusal");
    }

    // ── User cap ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Under_user_cap_allows_create(int currentCount)
    {
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxUsers: 5, expiresUtc: now.AddDays(30));

        LicenseService.EvaluateUserCreate(result, currentCount, now).Should().BeNull();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void At_or_above_user_cap_refuses_create(int currentCount)
    {
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxUsers: 5, expiresUtc: now.AddDays(30));

        var refusal = LicenseService.EvaluateUserCreate(result, currentCount, now);

        refusal.Should().NotBeNull()
            .And.StartWith("User limit reached")
            .And.Contain($"({currentCount}/5)");
    }

    [Fact]
    public void Zero_user_cap_means_unlimited()
    {
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(maxUsers: 0, expiresUtc: now.AddDays(30));

        LicenseService.EvaluateUserCreate(result, 1_000, now).Should().BeNull();
    }

    // ── Independence ───────────────────────────────────────────────────────

    [Fact]
    public void Target_cap_check_does_not_consider_user_count()
    {
        // Pin that the two caps are independent — being at the user cap
        // doesn't block target creation, and vice versa. A buggy refactor
        // that ANDs the two refusals together would block both paths the
        // moment ONE cap is hit.
        var now = DateTimeOffset.UtcNow;
        var result = ResultWith(
            maxTargets: 10, maxUsers: 5,
            expiresUtc: now.AddDays(30));

        // 4 users (under 5), 5 targets (under 10): both should allow.
        LicenseService.EvaluateUserCreate(result, 4, now).Should().BeNull();
        LicenseService.EvaluateTargetCreate(result, 5, now).Should().BeNull();
    }

    [Fact]
    public void Expiry_refusal_outranks_cap_refusal()
    {
        // If both expiry AND cap are over, expiry wins — there's no point
        // telling the operator "buy more targets" if their whole license
        // is dead.
        var now = DateTimeOffset.UtcNow;
        var expiredAndOver = ResultWith(
            maxTargets: 5, expiresUtc: now.AddDays(-1));

        var refusal = LicenseService.EvaluateTargetCreate(expiredAndOver, 100, now);

        refusal.Should().Contain("expired",
            "expiry refusal must outrank the cap refusal — fixing the cap " +
            "with an expired license fixes nothing");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static LicenseValidationResult ResultWith(
        int maxTargets = 100,
        int maxUsers = 100,
        DateTimeOffset? expiresUtc = null,
        LicenseType type = LicenseType.Full)
    {
        var expires = expiresUtc ?? DateTimeOffset.UtcNow.AddDays(30);
        return new LicenseValidationResult(
            IsValid: true,
            Claims: new LicenseClaims(
                CustomerName: "Test Customer",
                MaxTargets:   maxTargets,
                MaxUsers:     maxUsers,
                ExpiresUtc:   expires,
                IssuedUtc:    expires.AddDays(-365),
                LicenseType:  type),
            ErrorMessage: null);
    }
}

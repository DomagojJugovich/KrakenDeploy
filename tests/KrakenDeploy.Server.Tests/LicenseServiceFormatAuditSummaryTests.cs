using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Services;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="LicenseService.FormatAuditSummary"/>. This is
/// the audit_entries.details string for License.Uploaded events — it MUST
/// not include the raw JWT (vendor-signed material; storing it would leak
/// it to anyone with audit-view permission).
/// </summary>
public sealed class LicenseServiceFormatAuditSummaryTests
{
    [Fact]
    public void Summary_includes_customer_type_expiry_caps()
    {
        var summary = LicenseService.FormatAuditSummary(
            new LicenseClaims(
                CustomerName: "LAUS d.o.o.",
                MaxTargets:   25,
                MaxUsers:     10,
                ExpiresUtc:   new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero),
                IssuedUtc:    new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
                LicenseType:  LicenseType.Full));

        summary.Should().Be(
            "Customer=LAUS d.o.o., Type=Full, Expires=2027-01-15, " +
            "MaxTargets=25, MaxUsers=10");
    }

    [Fact]
    public void Zero_caps_render_as_unlimited_word()
    {
        var summary = LicenseService.FormatAuditSummary(
            new LicenseClaims(
                CustomerName: "Internal",
                MaxTargets:   0,
                MaxUsers:     0,
                ExpiresUtc:   new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
                IssuedUtc:    DateTimeOffset.UtcNow,
                LicenseType:  LicenseType.Developer));

        summary.Should().Contain("MaxTargets=unlimited")
               .And.Contain("MaxUsers=unlimited",
                "0 means 'no cap' (the LicenseService convention); audit " +
                "readers shouldn't have to know the magic-number rule");
    }

    [Fact]
    public void Summary_never_contains_jwt_dot_separators()
    {
        // Belt-and-braces: the summary should be a one-line label, never the
        // raw key. A JWT has at least two '.' separators between
        // header.payload.signature — if any of those slip in, we've leaked
        // signed material to the audit log.
        var summary = LicenseService.FormatAuditSummary(
            new LicenseClaims(
                CustomerName: "Demo",
                MaxTargets:   1,
                MaxUsers:     1,
                ExpiresUtc:   DateTimeOffset.UtcNow.AddDays(30),
                IssuedUtc:    DateTimeOffset.UtcNow,
                LicenseType:  LicenseType.Trial));

        // Customer / expiry strings can legitimately contain '.' so we
        // count the number — JWTs need two minimum.
        summary.Count(c => c == '.').Should().BeLessThan(2,
            "two or more dots strongly suggests a JWT slipped through");
    }

    [Fact]
    public void Null_claims_throws_ArgumentNullException()
    {
        var act = () => LicenseService.FormatAuditSummary(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Summary_uses_invariant_date_format()
    {
        // The audit log is operator-readable and search-friendly. yyyy-MM-dd
        // is unambiguous across cultures (no DMY vs MDY confusion) and sorts
        // correctly as a string. Pin this so a future culture-aware refactor
        // doesn't accidentally inject "1/15/2027" into operator searches.
        var summary = LicenseService.FormatAuditSummary(
            new LicenseClaims(
                CustomerName: "Test",
                MaxTargets:   10,
                MaxUsers:     5,
                ExpiresUtc:   new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
                IssuedUtc:    DateTimeOffset.UtcNow,
                LicenseType:  LicenseType.Full));

        summary.Should().Contain("Expires=2026-12-31");
    }
}

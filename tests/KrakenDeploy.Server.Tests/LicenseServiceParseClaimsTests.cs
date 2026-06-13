using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Services;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Regression test for the license fail-closed bug: <c>ParseClaims</c> parsed the
/// Unix-epoch <c>exp</c>/<c>iat</c> claims with <c>DateTimeOffset.Parse</c> (which
/// expects a date string), throwing a <c>FormatException</c>; ValidateLicense's
/// catch-all turned that into "invalid license", so a valid, paid license failed
/// every validation and blocked all target/user provisioning.
/// </summary>
public sealed class LicenseServiceParseClaimsTests
{
    [Fact]
    public void ParseClaims_reads_unix_epoch_exp_and_iat()
    {
        var expires = DateTimeOffset.Parse("2030-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var issued = DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture);

        // exp/iat are surfaced by JwtSecurityTokenHandler as Unix-second strings.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("customer_name", "Acme Gov"),
            new Claim("max_targets", "25"),
            new Claim("max_users", "10"),
            new Claim("license_type", "Full"),
            new Claim("exp", expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new Claim("iat", issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        ]));

        // Before the fix this threw FormatException (parsing an epoch as a date).
        var claims = LicenseService.ParseClaims(principal);

        claims.CustomerName.Should().Be("Acme Gov");
        claims.MaxTargets.Should().Be(25);
        claims.MaxUsers.Should().Be(10);
        claims.LicenseType.Should().Be(LicenseType.Full);
        claims.ExpiresUtc.Should().Be(expires);
        claims.IssuedUtc.Should().Be(issued);
    }
}

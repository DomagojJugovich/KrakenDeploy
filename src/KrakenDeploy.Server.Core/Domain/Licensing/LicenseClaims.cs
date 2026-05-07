namespace KrakenDeploy.Server.Core.Domain.Licensing;

/// <summary>
/// Claims embedded in a KrakenDeploy license key (RSA-signed JWT).
/// </summary>
public sealed record LicenseClaims(
    /// <summary>Customer / company name displayed in the license banner.</summary>
    string CustomerName,
    /// <summary>Maximum number of deployment targets across all Spaces.</summary>
    int MaxTargets,
    /// <summary>Maximum number of user accounts.</summary>
    int MaxUsers,
    /// <summary>UTC timestamp when the license expires.</summary>
    DateTimeOffset ExpiresUtc,
    /// <summary>UTC timestamp when the license was issued.</summary>
    DateTimeOffset IssuedUtc,
    /// <summary>Trial, Full, or Developer.</summary>
    LicenseType LicenseType);

/// <summary>Type of KrakenDeploy license.</summary>
public enum LicenseType
{
    /// <summary>Time-limited trial (typically 14 days).</summary>
    Trial = 0,
    /// <summary>Paid full license with support.</summary>
    Full = 1,
    /// <summary>Personal / non-production use. Max 5 targets, 3 users.</summary>
    Developer = 2,
}

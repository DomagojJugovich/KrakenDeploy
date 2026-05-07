namespace KrakenDeploy.Server.Core.Domain.Licensing;

/// <summary>
/// Result of validating a license key.
/// </summary>
public sealed record LicenseValidationResult(
    bool IsValid,
    LicenseClaims? Claims,
    string? ErrorMessage);

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using KrakenDeploy.Server.Core.Domain.Licensing;
using Microsoft.IdentityModel.Tokens;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Validates RSA-signed JWT license keys. Air-gapped — public key is embedded,
/// no phone-home. The private key is held by the license issuer (vendor), not
/// the server.
/// </summary>
public class LicenseService
{
    private readonly RSA _rsa;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LicenseService> _logger;
    private LicenseValidationResult? _cachedResult;

    // Embedded RSA public key (2048-bit). Generated for development — replace
    // with your own production key pair before shipping.
    private const string EmbeddedPublicKey =
        "<RSAKeyValue>" +
        "<Modulus>pWmpiQ8sCAlG2TmxIzqnYmeVES1oOsNBp90XR4bAP/IbuLlsoRZvRo9Awml/5oK2I/MyWhB/C/9uQ" +
        "KNq4+kT2xKC946+Rq+SmoNBed0M/D2X6k9EeLpnGxCDGrYPintLSlbtAd2zvmD7k7h/a/LCqdbwZKjwzO" +
        "5+huufMNrSj6z+DgoWcpij2YHAqwFGxE7nR41ObXm7FO8c0rBJS4Kd5Mh9ZzHyVEgT45cJa5ezpsqw+Y9" +
        "3V/8N+JGSGqFRZeBtD0ZVZHnXA01PdihEPvl9iSVwjm2wM51rCRejqlm4UYZtR/3KQk8vVS/DuUkC3D4e" +
        "3DbGzM52OJHqJwGqfAGbCQ==</Modulus>" +
        "<Exponent>AQAB</Exponent></RSAKeyValue>";

    public LicenseService(IConfiguration configuration, ILogger<LicenseService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _rsa = RSA.Create();
        _rsa.FromXmlString(EmbeddedPublicKey);
        _rsa.KeySize = 2048;
    }

    /// <summary>
    /// Validates a license key string. Returns the cached result on subsequent
    /// calls during the same process lifetime.
    /// </summary>
    public LicenseValidationResult ValidateLicense(string licenseKey)
    {
        if (_cachedResult is not null)
        {
            return _cachedResult;
        }

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            _cachedResult = new LicenseValidationResult(false, null, "License key is empty.");
            return _cachedResult;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(_rsa),
                ClockSkew = TimeSpan.FromMinutes(1),
            };

            var principal = handler.ValidateToken(licenseKey.Trim(), validationParams, out _);
            var claims = ParseClaims(principal);
            _cachedResult = new LicenseValidationResult(true, claims, null);
            _logger.LogInformation("License validated: {Customer}, {Type}, expires {Expires}.",
                claims.CustomerName, claims.LicenseType, claims.ExpiresUtc);
        }
        catch (SecurityTokenExpiredException)
        {
            _cachedResult = new LicenseValidationResult(false, null, "License has expired.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "License validation failed.");
            _cachedResult = new LicenseValidationResult(false, null, $"Invalid license: {ex.Message}");
        }

        return _cachedResult;
    }

    /// <summary>
    /// Loads the license key from <c>KRAKEN_LICENSE_KEY</c> environment variable
    /// or a <c>license.key</c> file in the data directory, validates it, and
    /// returns the result.
    /// </summary>
    public LicenseValidationResult LoadAndValidate()
    {
        var key = _configuration["KRAKEN_LICENSE_KEY"]
            ?? _configuration["License:Key"];

        if (string.IsNullOrWhiteSpace(key))
        {
            var dataPath = _configuration["Server:DataPath"] ?? "data";
            var filePath = Path.Combine(dataPath, "license.key");
            if (File.Exists(filePath))
            {
                key = File.ReadAllText(filePath).Trim();
            }
        }

        return ValidateLicense(key ?? string.Empty);
    }

    /// <summary>
    /// Returns a user-facing warning string if the license is absent, expired,
    /// approaching expiry (within 30 days), or approaching limit thresholds
    /// (90%+ of max targets or users). Returns null when all is well.
    /// </summary>
    public string? GetLicenseWarning(int currentTargetCount, int currentUserCount)
    {
        // If not yet loaded, try to load now.
        if (_cachedResult is null)
        {
            LoadAndValidate();
        }

        if (_cachedResult is null || !_cachedResult.IsValid)
        {
            return "No valid license. Upload a license key in Settings → License.";
        }

        var claims = _cachedResult.Claims!;
        var daysLeft = (claims.ExpiresUtc - DateTimeOffset.UtcNow).TotalDays;

        if (daysLeft <= 0)
        {
            return "License has expired. Upload a new license key.";
        }

        if (daysLeft <= 30)
        {
            return $"License expires in {(int)daysLeft} day{((int)daysLeft == 1 ? "" : "s")}. " +
                   "Contact support for a renewal.";
        }

        if (claims.MaxTargets > 0)
        {
            double ratio = (double)currentTargetCount / claims.MaxTargets;
            if (ratio >= 1.0)
            {
                return $"Target limit reached ({currentTargetCount}/{claims.MaxTargets}). " +
                       "Upgrade your license to add more targets.";
            }

            if (ratio >= 0.9)
            {
                return $"Approaching target limit ({currentTargetCount}/{claims.MaxTargets}). " +
                       "Consider upgrading your license.";
            }
        }

        if (claims.MaxUsers > 0)
        {
            double ratio = (double)currentUserCount / claims.MaxUsers;
            if (ratio >= 1.0)
            {
                return $"User limit reached ({currentUserCount}/{claims.MaxUsers}). " +
                       "Upgrade your license to add more users.";
            }

            if (ratio >= 0.9)
            {
                return $"Approaching user limit ({currentUserCount}/{claims.MaxUsers}).";
            }
        }

        return null;
    }

    /// <summary>
    /// Clears the cached validation result so the next call to
    /// <see cref="ValidateLicense"/> re-validates. Use after uploading a new key.
    /// </summary>
    public void ClearCache()
    {
        _cachedResult = null;
    }

    private static LicenseClaims ParseClaims(ClaimsPrincipal principal)
    {
        var customerName = principal.FindFirstValue("customer_name") ?? "Unknown";
        var maxTargets = int.Parse(
            principal.FindFirstValue("max_targets") ?? "0", CultureInfo.InvariantCulture);
        var maxUsers = int.Parse(
            principal.FindFirstValue("max_users") ?? "0", CultureInfo.InvariantCulture);
        var expiresUtc = DateTimeOffset.Parse(
            principal.FindFirstValue("exp") ?? "0", CultureInfo.InvariantCulture);
        var issuedUtc = DateTimeOffset.Parse(
            principal.FindFirstValue("iat") ?? "0", CultureInfo.InvariantCulture);
        var licenseType = Enum.Parse<LicenseType>(
            principal.FindFirstValue("license_type") ?? "Trial");

        // JWT exp/iat are Unix epoch seconds.
        var expDt = DateTimeOffset.UnixEpoch.AddSeconds(
            long.Parse(principal.FindFirstValue("exp") ?? "0", CultureInfo.InvariantCulture));
        var iatDt = DateTimeOffset.UnixEpoch.AddSeconds(
            long.Parse(principal.FindFirstValue("iat") ?? "0", CultureInfo.InvariantCulture));

        return new LicenseClaims(customerName, maxTargets, maxUsers, expDt, iatDt, licenseType);
    }
}

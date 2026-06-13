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
public class LicenseService : ILicenseGate
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
            var filePath = GetLicenseFilePath();
            if (File.Exists(filePath))
            {
                key = File.ReadAllText(filePath).Trim();
            }
        }

        return ValidateLicense(key ?? string.Empty);
    }

    /// <summary>
    /// Validates the pasted key. On success persists it to
    /// <c>data/license.key</c> so the activation survives restart, clears the
    /// cached validation result so the next read picks up the new key, and
    /// returns the validation result. On failure nothing is persisted — the
    /// previous key (if any) remains active.
    /// <para>
    /// Will refuse to overwrite if <c>KRAKEN_LICENSE_KEY</c> or
    /// <c>License:Key</c> is set in config (those take precedence over the
    /// file), because writing the file in that case has no effect and would
    /// silently mislead the operator into thinking they activated something.
    /// </para>
    /// </summary>
    public async Task<LicenseValidationResult> SaveAndActivateAsync(
        string licenseKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseKey);

        // Block when an environment/config override is set — saving the file
        // would not change runtime behaviour because LoadAndValidate reads
        // config first.
        if (!string.IsNullOrWhiteSpace(_configuration["KRAKEN_LICENSE_KEY"]) ||
            !string.IsNullOrWhiteSpace(_configuration["License:Key"]))
        {
            throw new InvalidOperationException(
                "A license key is provided via KRAKEN_LICENSE_KEY or " +
                "License:Key in configuration. Remove the environment / config " +
                "override before activating a key from the UI — otherwise the " +
                "saved file would be ignored.");
        }

        // Validate FIRST so an invalid paste doesn't trample a good file.
        ClearCache();
        var result = ValidateLicense(licenseKey);
        if (!result.IsValid)
        {
            return result;
        }

        var filePath = GetLicenseFilePath();
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(filePath, licenseKey.Trim(), ct).ConfigureAwait(false);

        // Already cached under the new key from ValidateLicense above —
        // banner + LicenseUsageCounter consumers are caller responsibility.
        _logger.LogInformation(
            "License activated and persisted to {Path}. Customer={Customer}, Type={Type}, Expires={Expires}.",
            filePath, result.Claims!.CustomerName, result.Claims.LicenseType, result.Claims.ExpiresUtc);
        return result;
    }

    /// <summary>
    /// Returns a short, audit-safe summary of the parsed license claims —
    /// customer name, type, expiry, caps. <em>Never</em> includes the raw JWT
    /// (that's vendor-signed material; storing it in <c>audit_entries</c>
    /// would leak it to anyone with audit-view permission).
    /// </summary>
    public static string FormatAuditSummary(LicenseClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var maxTargets = claims.MaxTargets == 0 ? "unlimited" : claims.MaxTargets.ToString(CultureInfo.InvariantCulture);
        var maxUsers   = claims.MaxUsers   == 0 ? "unlimited" : claims.MaxUsers.ToString(CultureInfo.InvariantCulture);
        return $"Customer={claims.CustomerName}, Type={claims.LicenseType}, " +
               $"Expires={claims.ExpiresUtc:yyyy-MM-dd}, " +
               $"MaxTargets={maxTargets}, MaxUsers={maxUsers}";
    }

    private string GetLicenseFilePath()
    {
        var dataPath = _configuration["Server:DataPath"] ?? "data";
        return Path.Combine(dataPath, "license.key");
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

    // ── ILicenseGate ───────────────────────────────────────────────────────
    // Enforcement is deliberately separate from GetLicenseWarning(): the
    // banner conflates expiry + cap warnings into one user-friendly string,
    // but a refuse-on-create gate needs to distinguish "expired" (refuse
    // everything) from "approaching limit" (allow, just warn). The methods
    // below treat the 90% threshold purely as a soft warning surface; the
    // hard stop only fires AT the cap (currentCount >= max).

    /// <inheritdoc />
    public string? CheckTargetCreate(int currentTargetCount)
    {
        if (_cachedResult is null) { LoadAndValidate(); }
        return EvaluateTargetCreate(_cachedResult, currentTargetCount, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string? CheckUserCreate(int currentUserCount)
    {
        if (_cachedResult is null) { LoadAndValidate(); }
        return EvaluateUserCreate(_cachedResult, currentUserCount, DateTimeOffset.UtcNow);
    }

    // ── Pure evaluation core ──────────────────────────────────────────────
    // These statics carry the gate logic and are unit-tested directly. They
    // take the validation result + current count + a clock value, so tests
    // can construct deterministic scenarios (no real time, no real JWT) and
    // production callers feed in `_cachedResult` + `DateTimeOffset.UtcNow`.

    internal static string? EvaluateTargetCreate(
        LicenseValidationResult? cachedResult, int currentTargetCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentTargetCount);
        var refusal = EvaluateLicenseHealth(cachedResult, now);
        if (refusal is not null) { return refusal; }

        var claims = cachedResult!.Claims!;
        if (claims.MaxTargets > 0 && currentTargetCount >= claims.MaxTargets)
        {
            return $"Target limit reached ({currentTargetCount}/{claims.MaxTargets}). " +
                   "Upgrade your license to add more targets.";
        }
        return null;
    }

    internal static string? EvaluateUserCreate(
        LicenseValidationResult? cachedResult, int currentUserCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentUserCount);
        var refusal = EvaluateLicenseHealth(cachedResult, now);
        if (refusal is not null) { return refusal; }

        var claims = cachedResult!.Claims!;
        if (claims.MaxUsers > 0 && currentUserCount >= claims.MaxUsers)
        {
            return $"User limit reached ({currentUserCount}/{claims.MaxUsers}). " +
                   "Upgrade your license to add more users.";
        }
        return null;
    }

    /// <summary>
    /// Common health gate for create operations. Returns null when the
    /// license is valid + unexpired (cap-specific checks are then layered
    /// on top), or a user-facing refusal message when the license itself
    /// is unusable.
    /// </summary>
    internal static string? EvaluateLicenseHealth(
        LicenseValidationResult? cachedResult, DateTimeOffset now)
    {
        if (cachedResult is null || !cachedResult.IsValid)
        {
            return "No valid license. Upload a license key in Settings → License " +
                   "before adding more resources.";
        }
        var claims = cachedResult.Claims!;
        if (claims.ExpiresUtc <= now)
        {
            return "License has expired. Upload a new license key to add more resources.";
        }
        return null;
    }

    internal static LicenseClaims ParseClaims(ClaimsPrincipal principal)
    {
        var customerName = principal.FindFirstValue("customer_name") ?? "Unknown";
        var maxTargets = int.Parse(
            principal.FindFirstValue("max_targets") ?? "0", CultureInfo.InvariantCulture);
        var maxUsers = int.Parse(
            principal.FindFirstValue("max_users") ?? "0", CultureInfo.InvariantCulture);
        var licenseType = Enum.Parse<LicenseType>(
            principal.FindFirstValue("license_type") ?? "Trial");

        // JWT exp/iat are Unix epoch seconds — DateTimeOffset.Parse would throw a
        // FormatException on the integer claim (it expects a date string), which
        // ValidateLicense's catch-all then turned into "invalid license", failing
        // every validation closed and blocking all provisioning. Parse as epoch.
        var expDt = DateTimeOffset.UnixEpoch.AddSeconds(
            long.Parse(principal.FindFirstValue("exp") ?? "0", CultureInfo.InvariantCulture));
        var iatDt = DateTimeOffset.UnixEpoch.AddSeconds(
            long.Parse(principal.FindFirstValue("iat") ?? "0", CultureInfo.InvariantCulture));

        return new LicenseClaims(customerName, maxTargets, maxUsers, expDt, iatDt, licenseType);
    }
}

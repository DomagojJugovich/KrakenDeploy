using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Simple API-key authentication for the <c>kraken</c> CLI.
/// Reads the <c>X-Api-Key</c> request header and validates it against
/// the <c>ApiKey:Key</c> configuration value.
/// When valid, synthesises a ClaimsPrincipal with a <c>cli</c> role so
/// that the standard <c>RequireAuthorization()</c> policy is satisfied.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header → let the next scheme try.
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var supplied = headerValues.ToString();
        var configured = configuration["ApiKey:Key"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            // API-key auth is not enabled — treat as NoResult so cookie auth can still work.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(supplied, configured, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "cli"),
            new Claim(ClaimTypes.Role, "cli"),
        };
        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

using System.Security.Claims;
using System.Text.Encodings.Web;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Per-user API-key authentication (M13.C.4) for the <c>kraken</c> CLI, the
/// MCP surface and REST callers. Reads the <c>X-Api-Key</c> header, recomputes
/// its SHA-256 and resolves the matching <c>api_keys</c> row — the static
/// <c>ApiKey:Key</c> configuration value is gone.
/// <para>
/// The synthesized principal carries the OWNER's id as
/// <see cref="ClaimTypes.NameIdentifier"/>, so <c>IPermissionEvaluator</c>
/// resolves the caller's real team/role grants — an API key can never do
/// more than its owner. A Space-restricted key additionally carries
/// <see cref="KrakenClaimTypes.ApiKeySpace"/>, which the evaluator enforces
/// as a hard cage on every permission check.
/// </para>
/// <para>
/// Missing header → <c>NoResult()</c> so cookie/OIDC schemes still run —
/// that chaining is what keeps browser login working; preserve it.
/// </para>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService apiKeys,
    ApiKeyUsageTracker usageTracker)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = KrakenAuthSchemes.ApiKey;
    private const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header → let the next scheme try.
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var result = await apiKeys.AuthenticateTokenAsync(
            headerValues.ToString(), Context.RequestAborted);

        switch (result.Status)
        {
            case ApiKeyAuthStatus.UnknownKey:
                // Uniform failure message — no enumeration oracle between
                // unknown/revoked/expired. The precise reason is log-only.
                Logger.LogWarning("API-key auth failed: no key matches the presented token.");
                return AuthenticateResult.Fail("Invalid API key.");

            case ApiKeyAuthStatus.Revoked:
                Logger.LogWarning(
                    "API-key auth failed: key {Prefix} ('{Name}') was revoked {RevokedUtc:u}.",
                    result.Key!.Prefix, result.Key.Name, result.Key.RevokedUtc);
                return AuthenticateResult.Fail("Invalid API key.");

            case ApiKeyAuthStatus.Expired:
                Logger.LogWarning(
                    "API-key auth failed: key {Prefix} ('{Name}') expired {ExpiresUtc:u}.",
                    result.Key!.Prefix, result.Key.Name, result.Key.ExpiresUtc);
                return AuthenticateResult.Fail("Invalid API key.");

            case ApiKeyAuthStatus.OwnerMissing:
                Logger.LogError(
                    "API-key auth failed CLOSED: key {Prefix} has no owning user row — " +
                    "keys should be deleted with their owner (UserService.DeleteAsync).",
                    result.Key!.Prefix);
                return AuthenticateResult.Fail("Invalid API key.");
        }

        var key = result.Key!;

        // Surface policy: enroll-only keys are refused everywhere until the
        // agent-enrollment flow (design-agent-enrollment-cert-auth.md) ships.
        if (key.Scope != ApiKeyScope.Full)
        {
            Logger.LogWarning(
                "API-key auth failed: key {Prefix} ('{Name}') has scope {Scope}, which no " +
                "surface accepts yet.", key.Prefix, key.Name, key.Scope);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        // Throttled last-used stamp: at most one UPDATE per key per window,
        // awaited inline so multi-account request routing stays intact.
        if (usageTracker.ShouldWrite(key.Id))
        {
            await apiKeys.TouchLastUsedAsync(key.Id, Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            // The owner's id — the ONE claim IPermissionEvaluator reads.
            new(ClaimTypes.NameIdentifier, key.UserId.ToString()),
            new(ClaimTypes.Name, result.OwnerUserName!),
            new(KrakenClaimTypes.ApiKeyId, key.Id.ToString()),
        };
        if (key.SpaceId is not null)
        {
            claims.Add(new Claim(KrakenClaimTypes.ApiKeySpace, key.SpaceId.Value.ToString()));

            // Pin the request's ambient Space to the key's Space so the whole
            // surface is coherent: Space-filtered queries, the permission
            // scope PermissionAuthorizationHandler defaults to, and the MCP
            // enabled-gate all see the SAME Space the evaluator cage allows.
            // Safe without an accessibility round-trip: the restriction was
            // validated at mint time and grants still come from the owner's
            // real role assignments in that Space.
            //
            // Guard: never repin a request that is ALREADY authenticated. This
            // handler also runs during authorization-time scheme evaluation, so
            // a request carrying both a cookie (the winning identity) and a
            // stray restricted X-Api-Key would otherwise have its Space silently
            // repinned by the unused key. Pure API requests have no prior
            // identity here, so the pin still fires for them.
            if (result.SpaceSlug is not null
                && Context.User?.Identity?.IsAuthenticated != true
                && Context.RequestServices.GetService<Core.Domain.Spaces.ISpaceContext>()
                    is Spaces.HttpSpaceContext httpSpace)
            {
                httpSpace.SetResolved(key.SpaceId.Value, result.SpaceSlug);
            }
        }

        var identity  = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

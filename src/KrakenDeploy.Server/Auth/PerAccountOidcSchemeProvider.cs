using KrakenDeploy.Server.Accounts;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Request-time <see cref="IAuthenticationSchemeProvider"/> for SaaS multi-account.
/// Subclasses the framework provider and synthesizes per-tenant OIDC schemes
/// (<c>oidc_{accountId:N}_{providerId:N}</c>) for the account resolved from the request
/// host (<see cref="AccountResolutionMiddleware"/> → <c>HttpContext.Items</c>). All
/// non-OIDC schemes — the cookie, external, agent-JWT, API-key schemes — delegate to
/// the base. The OIDC handler + framework machinery are registered once via the
/// sentinel template scheme (<see cref="OidcRegistrar.MultiAccountTemplateScheme"/>),
/// which is excluded from request-handler resolution so it never runs.
/// </summary>
public sealed class PerAccountOidcSchemeProvider : AuthenticationSchemeProvider
{
    private readonly IHttpContextAccessor _http;
    private readonly PerAccountOidcProviderCache _providers;

    public PerAccountOidcSchemeProvider(
        IOptions<AuthenticationOptions> options,
        IHttpContextAccessor http,
        PerAccountOidcProviderCache providers)
        : base(options)
    {
        _http = http;
        _providers = providers;
    }

    public override async Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (OidcRegistrar.TryParseMultiAccountScheme(name, out var accountId, out var providerId))
        {
            // Synthesize only for a provider that actually exists + is enabled for the
            // account — an unknown pair is not a valid scheme (fail closed).
            return await _providers.IsEnabledAsync(accountId, providerId).ConfigureAwait(false)
                ? Synthesize(name)
                : null;
        }

        return await base.GetSchemeAsync(name).ConfigureAwait(false);
    }

    public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
    {
        // Exclude the sentinel template (it exists only to register the handler), so its
        // OIDC handler is never instantiated and its options are never resolved.
        var baseHandlers = (await base.GetRequestHandlerSchemesAsync().ConfigureAwait(false))
            .Where(s => !string.Equals(
                s.Name, OidcRegistrar.MultiAccountTemplateScheme, StringComparison.Ordinal));

        var callback = await ResolveCallbackSchemeAsync().ConfigureAwait(false);
        return callback is null ? baseHandlers : baseHandlers.Append(callback);
    }

    public override async Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
    {
        var all = (await base.GetAllSchemesAsync().ConfigureAwait(false))
            .Where(s => !string.Equals(
                s.Name, OidcRegistrar.MultiAccountTemplateScheme, StringComparison.Ordinal))
            .ToList();

        var account = CurrentAccount();
        if (account is not null)
        {
            foreach (var providerId in
                     await _providers.GetEnabledProviderIdsAsync(account.Id).ConfigureAwait(false))
            {
                all.Add(Synthesize(OidcRegistrar.SchemeName(account.Id, providerId)));
            }
        }

        return all;
    }

    // Returns the per-account scheme matching the current request's callback path, but
    // ONLY when it belongs to the host-resolved account (defense in depth — the OIDC
    // correlation cookie is host-only, so a cross-account callback already fails).
    private async Task<AuthenticationScheme?> ResolveCallbackSchemeAsync()
    {
        var path = _http.HttpContext?.Request.Path.Value;
        if (path is null || !path.StartsWith("/signin-oidc_", StringComparison.Ordinal))
        {
            return null;
        }

        var schemeName = path["/signin-".Length..]; // -> "oidc_{accountId}_{providerId}"
        if (!OidcRegistrar.TryParseMultiAccountScheme(schemeName, out var accountId, out var providerId))
        {
            return null;
        }

        var account = CurrentAccount();
        if (account is null || account.Id != accountId)
        {
            return null;
        }

        return await _providers.IsEnabledAsync(accountId, providerId).ConfigureAwait(false)
            ? Synthesize(schemeName)
            : null;
    }

    private ResolvedAccount? CurrentAccount() =>
        _http.HttpContext?.Items[HttpAccountContext.ItemsKey] as ResolvedAccount;

    private static AuthenticationScheme Synthesize(string name) =>
        new(name, displayName: name, handlerType: typeof(OpenIdConnectHandler));
}

using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Multi-account <see cref="IOidcSchemeCacheInvalidator"/>. When an IdP is edited in the
/// current account's context, evicts the cached <see cref="OpenIdConnectOptions"/> for
/// that scheme (so the next sign-in reloads the new config) and the per-account
/// enabled-provider cache (so the login page + scheme provider see add/remove/disable
/// immediately) — no farm restart needed. Scoped, because it reads the request-resolved
/// <see cref="IAccountContext"/>.
/// </summary>
public sealed class OidcSchemeCacheInvalidator(
    IAccountContext accountContext,
    PerAccountOidcProviderCache providerCache,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache) : IOidcSchemeCacheInvalidator
{
    public void Invalidate(Guid providerId)
    {
        // IdP administration is always per-tenant; if no account is resolved (e.g. a
        // control-plane path) there is nothing per-account to evict.
        if (!accountContext.IsResolved)
        {
            return;
        }

        var accountId = accountContext.CurrentAccountId;
        optionsCache.TryRemove(OidcRegistrar.SchemeName(accountId, providerId));
        providerCache.Evict(accountId);
    }
}

using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Tenant-keyed <see cref="OpenIdConnectOptions"/> configurer for SaaS multi-account.
/// When <c>IOptionsMonitor</c> first resolves a per-account scheme name
/// (<c>oidc_{accountId:N}_{providerId:N}</c>), this loads that provider from the owning
/// account's tenant database, decrypts the client secret, and configures the options —
/// reusing <see cref="OidcRegistrar.BuildEvents"/> verbatim. No-ops on any other name
/// (the sentinel template scheme, the single-instance schemes, non-OIDC schemes).
/// <para>
/// Runs once per scheme name and is then cached by <c>IOptionsMonitor</c> (evicted on
/// IdP edit by <c>OidcSchemeCacheInvalidator</c>). <c>IConfigureNamedOptions.Configure</c>
/// is synchronous, so the one-time resolve + DB read is sync-over-async — bounded and
/// acceptable here.
/// </para>
/// </summary>
public sealed class PerAccountOidcConfigureOptions(IServiceScopeFactory scopeFactory)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (!OidcRegistrar.TryParseMultiAccountScheme(name, out var accountId, out var providerId))
        {
            return; // not a per-account scheme — leave to its own configurer
        }

        using var scope = scopeFactory.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IAccountResolver>();
        var account = resolver.ResolveByIdAsync(accountId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                $"OIDC scheme '{name}': business account {accountId} not found (fail closed).");

        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContext>();
        using (accountContext.WithAccount(account))
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
            using var db = dbFactory.CreateDbContext();

            var idp = db.IdentityProviders.FirstOrDefault(p => p.Id == providerId && p.IsEnabled);
            if (idp is null
                || idp.Authority is null
                || idp.ClientId is null
                || idp.ClientSecretEncrypted is null)
            {
                throw new InvalidOperationException(
                    $"OIDC scheme '{name}': provider {providerId} is missing, disabled, or " +
                    "incomplete (fail closed).");
            }

            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
            var secret = encryption.Decrypt(idp.ClientSecretEncrypted);

            options.Authority    = idp.Authority;
            options.ClientId     = idp.ClientId;
            options.ClientSecret = secret;
            options.ResponseType = "code";
            options.UsePkce      = true;
            options.SaveTokens   = false;

            // Per-scheme callback path (matches the single-instance convention).
            options.CallbackPath = $"/signin-{name}";
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.GetClaimsFromUserInfoEndpoint = true;

            options.Scope.Clear();
            foreach (var s in idp.Scopes.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                options.Scope.Add(s);
            }

            options.Events = OidcRegistrar.BuildEvents(
                idp.Id, idp.Name, name!, idp.AutoProvisionUsers, idp.GroupClaimName, idp.DefaultTeamId);
        }
    }

    // Named-only: the parameterless path is never the per-account case.
    public void Configure(OpenIdConnectOptions options) { }
}

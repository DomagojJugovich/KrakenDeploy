using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Auth;

/// <summary>
/// Registers one named OIDC authentication scheme per enabled
/// <see cref="IdentityProvider"/> row found in the database at startup.
/// <para>
/// Called during the composition root (before <c>builder.Build()</c>), so
/// new or modified identity providers require a server restart to take effect
/// — identical behaviour to Octopus Deploy.
/// </para>
/// </summary>
public static class OidcRegistrar
{
    /// <summary>
    /// Derives the ASP.NET Core authentication scheme name for a given provider.
    /// </summary>
    public static string SchemeName(Guid providerId) => $"oidc_{providerId:N}";

    /// <summary>
    /// Loads all enabled <see cref="IdentityProvider"/> rows and wires up one
    /// <c>AddOpenIdConnect</c> call per provider.  Silently no-ops if the DB is
    /// not yet reachable (first-run / migration pending) so startup still
    /// succeeds.
    /// </summary>
    public static void RegisterSchemes(WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var masterKey = builder.Configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            return;
        }

        // Build a throw-away scope to query the DB.  The data project's
        // DefaultSpaceContext is registered by AddKrakenDeployData and
        // satisfies ISpaceContext without an HTTP request in scope.
        using var tempProvider = builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = false });
        using var scope = tempProvider.CreateScope();

        var db         = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

        List<IdentityProvider> providers;
        try
        {
            providers = db.IdentityProviders
                .Where(p => p.IsEnabled
                         && p.Authority != null
                         && p.ClientId != null
                         && p.ClientSecretEncrypted != null)
                .ToList();
        }
        catch (Exception ex)
        {
            // DB not yet migrated / not reachable — skip OIDC; local login still works.
            var logger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OidcRegistrar));
            logger.LogWarning(ex,
                "OIDC scheme registration skipped — could not query IdentityProviders " +
                "({Message}). Local-account login is still available.",
                ex.Message);
            return;
        }

        if (providers.Count == 0)
        {
            return;
        }

        var authBuilder = builder.Services.AddAuthentication();

        foreach (var idp in providers)
        {
            string secret;
            try
            {
                secret = encryption.Decrypt(idp.ClientSecretEncrypted!);
            }
            catch
            {
                continue; // Skip providers whose secret can't be decrypted
            }

            var scheme = SchemeName(idp.Id);
            var idpId   = idp.Id;
            var idpName = idp.Name;
            var autoProvision    = idp.AutoProvisionUsers;
            var groupClaimName   = idp.GroupClaimName;
            var scopes           = idp.Scopes;

            authBuilder.AddOpenIdConnect(scheme, idpName, options =>
            {
                options.Authority    = idp.Authority;
                options.ClientId     = idp.ClientId;
                options.ClientSecret = secret;
                options.ResponseType = "code";
                options.UsePkce      = true;
                options.SaveTokens   = false;

                // Each scheme needs its own callback path to avoid conflicts.
                options.CallbackPath = $"/signin-{scheme}";

                // Use the external (short-lived) cookie as the interim store;
                // our OnTicketReceived handler replaces it with the full
                // Identity application cookie before responding to the client.
                options.SignInScheme = IdentityConstants.ExternalScheme;

                options.GetClaimsFromUserInfoEndpoint = true;

                options.Scope.Clear();
                foreach (var s in scopes.Split(' ',
                             StringSplitOptions.RemoveEmptyEntries |
                             StringSplitOptions.TrimEntries))
                {
                    options.Scope.Add(s);
                }

                options.Events = BuildEvents(idpId, idpName, scheme,
                    autoProvision, groupClaimName);
            });
        }
    }

    // ── Event handler factory ─────────────────────────────────────────────────

    private static OpenIdConnectEvents BuildEvents(
        Guid idpId,
        string idpName,
        string scheme,
        bool autoProvision,
        string groupClaimName)
    {
        return new OpenIdConnectEvents
        {
            OnTicketReceived = async context =>
            {
                var services     = context.HttpContext.RequestServices;
                var userManager  = services.GetRequiredService<UserManager<ApplicationUser>>();
                var signInMgr    = services.GetRequiredService<SignInManager<ApplicationUser>>();
                var logger       = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OidcRegistrar));

                // ── 1. Resolve email ──────────────────────────────────────────
                var principal = context.Principal;
                var email     =
                    principal?.FindFirstValue(ClaimTypes.Email) ??
                    principal?.FindFirstValue("email") ??
                    principal?.FindFirstValue("preferred_username");

                if (string.IsNullOrWhiteSpace(email))
                {
                    logger.LogWarning(
                        "OIDC [{Scheme}]: token contains no email / preferred_username.",
                        scheme);
                    context.Response.Redirect("/login?error=no_email");
                    context.HandleResponse();
                    return;
                }

                // ── 2. Resolve the stable subject + email-verification state ──
                // The IdP-asserted subject (sub) is the stable identity key. We
                // match on (provider scheme, sub) FIRST so a changed/recycled
                // email — or a second IdP asserting someone else's email — can't
                // take over an account. email_verified gates the email-based path
                // only when it is EXPLICITLY false (ADFS/LDAP may omit it, so we
                // must not hard-require it).
                var sub =
                    principal?.FindFirstValue("sub") ??
                    principal?.FindFirstValue(ClaimTypes.NameIdentifier);

                var emailExplicitlyUnverified =
                    bool.TryParse(principal?.FindFirstValue("email_verified"), out var ev) && !ev;

                // ── 3. Find or provision user ─────────────────────────────────
                ApplicationUser? user = null;
                if (!string.IsNullOrWhiteSpace(sub))
                {
                    user = await userManager.FindByLoginAsync(scheme, sub);
                }
                var matchedBySub = user is not null;

                if (user is null)
                {
                    // No linked login yet — fall back to email (first sign-in via
                    // this provider, or a pre-existing local / invited account).
                    // Refuse an EXPLICITLY-unverified email here: that is the claim
                    // an attacker would forge to hijack an email-matched account.
                    if (emailExplicitlyUnverified)
                    {
                        logger.LogWarning(
                            "OIDC [{Scheme}]: sign-in refused for {Email} — email_verified " +
                            "is false and no linked login exists.", scheme, email);
                        context.Response.Redirect("/login?error=email_unverified");
                        context.HandleResponse();
                        return;
                    }

                    user = await userManager.FindByEmailAsync(email);
                }

                // Service accounts authenticate ONLY via API keys — block SSO
                // sign-in even if a matching row exists (by sub OR email). Without
                // this, an IdP user whose email collides with a service account
                // would inherit its team membership (a real escalation risk).
                if (user is not null && user.Kind == UserKind.ServiceAccount)
                {
                    logger.LogWarning(
                        "OIDC [{Scheme}]: sign-in refused for {Email} — that " +
                        "username belongs to a service account; service accounts " +
                        "authenticate via API keys only.",
                        scheme, email);
                    context.Response.Redirect("/login?error=service_account_no_sso");
                    context.HandleResponse();
                    return;
                }

                if (user is null)
                {
                    if (!autoProvision)
                    {
                        context.Response.Redirect("/login?error=not_provisioned");
                        context.HandleResponse();
                        return;
                    }

                    // License-cap gate. Without it, an unmetered IdP can
                    // silently mint users past the license's MaxUsers (just
                    // by letting people log in once). Counts against the
                    // global Identity table — same metric as InviteAsync.
                    var licenseGate = services.GetRequiredService<ILicenseGate>();
                    var currentUsers = await userManager.Users.CountAsync();
                    var capRefusal = licenseGate.CheckUserCreate(currentUsers);
                    if (capRefusal is not null)
                    {
                        logger.LogWarning(
                            "OIDC [{Scheme}]: JIT provisioning refused for {Email} — {Reason}",
                            scheme, email, capRefusal);
                        context.Response.Redirect("/login?error=license_limit");
                        context.HandleResponse();
                        return;
                    }

                    user = new ApplicationUser
                    {
                        Email          = email,
                        UserName       = email,
                        EmailConfirmed = true,
                    };

                    var created = await userManager.CreateAsync(user);
                    if (!created.Succeeded)
                    {
                        logger.LogError(
                            "OIDC [{Scheme}]: JIT provisioning failed for {Email}: {Errors}",
                            scheme, email,
                            string.Join("; ", created.Errors.Select(e => e.Description)));
                        context.Response.Redirect("/login?error=provision_failed");
                        context.HandleResponse();
                        return;
                    }

                    logger.LogInformation(
                        "OIDC [{Scheme}]: JIT-provisioned user {Email} (provider {IdpName}).",
                        scheme, email, idpName);
                }

                // Link the external login on first sign-in via this provider so
                // every subsequent sign-in matches on (scheme, sub), not email.
                if (!matchedBySub && !string.IsNullOrWhiteSpace(sub))
                {
                    var link = await userManager.AddLoginAsync(
                        user, new UserLoginInfo(scheme, sub, idpName));
                    if (!link.Succeeded)
                    {
                        logger.LogWarning(
                            "OIDC [{Scheme}]: could not link (scheme, sub) login for {Email}: {Errors}",
                            scheme, email,
                            string.Join("; ", link.Errors.Select(e => e.Description)));
                    }
                }

                // ── 4. Persist external group memberships ─────────────────────
                // Stored on the user record so they survive Identity security-
                // stamp refreshes without requiring the IdP to be re-queried.
                var groups = principal!
                    .FindAll(groupClaimName)
                    .Select(c => c.Value.Replace("|", ""))   // pipe is our separator
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct()
                    .ToList();

                user.LastOidcProviderId = idpId;
                user.ExternalGroups     = groups.Count > 0
                    ? string.Join('|', groups)
                    : null;

                await userManager.UpdateAsync(user);

                // ── 5. Sign in with the Identity application cookie ───────────
                await signInMgr.SignInAsync(user, isPersistent: true);

                var returnUrl = context.Properties?.RedirectUri ?? "/";
                context.Response.Redirect(returnUrl);
                context.HandleResponse();
            },

            OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>().CreateLogger(nameof(OidcRegistrar));
                logger.LogWarning(context.Failure,
                    "OIDC [{Scheme}]: remote authentication failure.", scheme);
                context.Response.Redirect("/login?error=remote_failure");
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }
}

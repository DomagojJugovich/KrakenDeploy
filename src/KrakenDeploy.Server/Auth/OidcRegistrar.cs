using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Net;
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
    /// Derives the ASP.NET Core authentication scheme name for a given provider
    /// (single-instance: one global DB, providerId alone is unique).
    /// </summary>
    public static string SchemeName(Guid providerId) => $"oidc_{providerId:N}";

    /// <summary>
    /// Multi-account scheme name. The accountId is encoded so the dynamic options
    /// configurer can resolve the owning tenant database directly; it is the immutable
    /// catalog account id (not the subdomain), so the name — and the IdP-registered
    /// redirect URI — survive subdomain renames / white-label custom domains.
    /// </summary>
    public static string SchemeName(Guid accountId, Guid providerId) =>
        $"oidc_{accountId:N}_{providerId:N}";

    /// <summary>
    /// Sentinel scheme that registers the <c>OpenIdConnectHandler</c> + framework
    /// post-configure machinery once in multi-account mode. It is never emitted by the
    /// login page nor challengeable (the dynamic scheme provider also excludes it from
    /// request-handler resolution), so its options are never resolved.
    /// </summary>
    public const string MultiAccountTemplateScheme = "__oidc_mt_template__";

    /// <summary>
    /// Parses a multi-account OIDC scheme name (<c>oidc_{accountId:N}_{providerId:N}</c>).
    /// Returns false for single-instance names (<c>oidc_{providerId:N}</c>) and anything
    /// that is not a per-account OIDC scheme.
    /// </summary>
    public static bool TryParseMultiAccountScheme(
        string? name, out Guid accountId, out Guid providerId)
    {
        accountId = Guid.Empty;
        providerId = Guid.Empty;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("oidc_", StringComparison.Ordinal))
        {
            return false;
        }

        // "oidc_" + 32-hex account + "_" + 32-hex provider.
        var rest = name.AsSpan("oidc_".Length);
        if (rest.Length != 32 + 1 + 32 || rest[32] != '_')
        {
            return false;
        }

        return Guid.TryParseExact(rest[..32], "N", out accountId)
            && Guid.TryParseExact(rest[33..], "N", out providerId);
    }

    /// <summary>
    /// Loads all enabled <see cref="IdentityProvider"/> rows and wires up one
    /// <c>AddOpenIdConnect</c> call per provider.  Silently no-ops if the DB is
    /// not yet reachable (first-run / migration pending) so startup still
    /// succeeds.
    /// </summary>
    public static void RegisterSchemes(WebApplicationBuilder builder, SsrfPolicy oidcSsrfPolicy)
    {
        // Multi-account: external IdPs are per-tenant (each account's own DB), so they
        // cannot be registered as process-wide startup schemes — a scheme is global,
        // tenants/IdPs added after startup wouldn't be picked up, and the startup query
        // has no resolved account. Per-account SSO is a separate Phase-4 design (central
        // auth domain / per-account customer SSO). Skip global registration here.
        if (builder.Configuration.GetValue("MultiAccount:Enabled", false))
        {
            return;
        }

        // Guard: only proceed when the app DB is configured. (This previously read the
        // never-configured "Default" connection name, which silently disabled ALL
        // external OIDC login even in single-instance — the real key is "KrakenDb".
        // The value is only a presence check; the KrakenDbContext below comes from DI.)
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var masterKey = builder.Configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            return;
        }

        // Query the DB by constructing the context + encryption DIRECTLY — do NOT call
        // builder.Services.BuildServiceProvider() here. Building the full provider during
        // composition resolves (and FREEZES) Serilog's logger before the real
        // builder.Build(), which then throws "the logger is already frozen". Constructing
        // the context directly avoids that (mirrors ResolveHangfireWorkerCount). The
        // pass-through DefaultSpaceContext satisfies ISpaceContext; no account override is
        // needed (this path is single-instance only — multi-account returned above).
        using var db = new KrakenDbContext(
            new DbContextOptionsBuilder<KrakenDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options,
            new KrakenDeploy.Server.Data.Spaces.DefaultSpaceContext());

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
            // ILogger isn't built yet at composition time, so write to the console.
            Console.Error.WriteLine(
                $"OIDC scheme registration skipped — could not query IdentityProviders " +
                $"({ex.Message}). Local-account login is still available.");
            return;
        }

        if (providers.Count == 0)
        {
            return;
        }

        // Envelope encryption (M13.D.2): unwrap the DEK with the KEK (masterKey)
        // using the directly-constructed context, then decrypt client secrets
        // with the DEK. Mirrors the direct-construction discipline above (no
        // BuildServiceProvider). No DEK row ⇒ nothing to decrypt.
        byte[] dek;
        try
        {
            var dekRow = db.DataEncryptionKeys.AsNoTracking()
                .FirstOrDefault(k => k.AccountId == null);
            if (dekRow is null)
            {
                return;
            }
            dek = KrakenDeploy.Server.Data.Encryption.DekProvider.Unwrap(
                Convert.FromBase64String(masterKey), dekRow.WrappedDek);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"OIDC scheme registration skipped — could not unwrap the data-encryption key " +
                $"({ex.Message}). Local-account login is still available.");
            return;
        }

        var authBuilder = builder.Services.AddAuthentication();

        // SSRF: the OIDC middleware fetches discovery/JWKS from the Authority over
        // its backchannel. Pin the validated IP per hop so an internal Authority
        // (or a redirect to one) can't be reached. Save-time validation in
        // IdentityProviderService is the first gate; this is the fetch-time backstop.
        foreach (var idp in providers)
        {
            string secret;
            try
            {
                secret = KrakenDeploy.Contracts.Crypto.AesGcmCipher.Decrypt(
                    dek, idp.ClientSecretEncrypted!);
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
                options.BackchannelHttpHandler =
                    SsrfHttpHandlerFactory.Create(oidcSsrfPolicy, allowAutoRedirect: true);

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
                    autoProvision, groupClaimName, idp.DefaultTeamId);
            });
        }
    }

    // ── Multi-account (SaaS) dynamic per-tenant registration ──────────────────

    /// <summary>
    /// Multi-account counterpart to <see cref="RegisterSchemes"/>. Instead of one
    /// startup scheme per provider from a single DB, it registers the OIDC handler
    /// machinery ONCE (a sentinel template scheme) plus the request-time
    /// <see cref="PerAccountOidcSchemeProvider"/> and tenant-keyed
    /// <see cref="PerAccountOidcConfigureOptions"/>, so each tenant's IdPs are resolved
    /// per request from that account's own database. See
    /// <c>docs/saas-per-account-sso.md</c>.
    /// </summary>
    public static void RegisterMultiAccountSchemes(WebApplicationBuilder builder)
    {
        // Register the OpenIdConnectHandler + framework post-configure once via a
        // sentinel scheme whose options are never resolved (the scheme provider excludes
        // it from request-handler resolution, and the login page never emits it). Dummy
        // Authority/CallbackPath keep it benign even if its options were ever resolved.
        builder.Services.AddAuthentication()
            .AddOpenIdConnect(MultiAccountTemplateScheme, options =>
            {
                options.Authority    = "https://oidc-template.invalid";
                options.ClientId     = "unused";
                options.CallbackPath = "/__oidc_mt_unused__";
            });

        // Dynamic per-tenant options: configures any oidc_{accountId}_{providerId} name
        // from the owning account's DB when IOptionsMonitor first resolves it.
        builder.Services.AddSingleton<
            Microsoft.Extensions.Options.IConfigureOptions<OpenIdConnectOptions>,
            PerAccountOidcConfigureOptions>();

        // Per-account enabled-provider cache (backs the scheme provider's existence
        // checks so the auth middleware never hits the tenant DB on the hot path).
        // AddMemoryCache is idempotent (TryAdd) — the catalog resolver also uses it.
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<PerAccountOidcProviderCache>();

        // Replace the default scheme provider with the request-time decorator that
        // synthesizes per-tenant OIDC schemes for the resolved account.
        builder.Services.AddSingleton<IAuthenticationSchemeProvider, PerAccountOidcSchemeProvider>();

        // Real scheme-cache evictor (overrides the no-op default registered by
        // AddKrakenDeployData) so an IdP edit applies without a restart.
        builder.Services.AddScoped<
            KrakenDeploy.Server.Core.Domain.Security.IOidcSchemeCacheInvalidator,
            OidcSchemeCacheInvalidator>();
    }

    // ── Event handler factory ─────────────────────────────────────────────────

    internal static OpenIdConnectEvents BuildEvents(
        Guid idpId,
        string idpName,
        string scheme,
        bool autoProvision,
        string groupClaimName,
        Guid? defaultTeamId)
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

                    // Auto-add the new user to the provider's default team, as the
                    // IdentityProvider.DefaultTeamId doc promises (previously the
                    // column was stored + configurable but never applied). Best-
                    // effort: a missing/duplicate team membership must not fail the
                    // sign-in. Only on JIT creation — existing users keep their teams.
                    if (defaultTeamId is { } teamId)
                    {
                        try
                        {
                            await using var db = await services
                                .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
                                .CreateDbContextAsync();
                            var teamExists = await db.Teams.AnyAsync(t => t.Id == teamId);
                            if (teamExists)
                            {
                                db.Set<TeamMember>().Add(new TeamMember
                                {
                                    TeamId = teamId,
                                    UserId = user.Id,
                                    AddedUtc = DateTimeOffset.UtcNow,
                                });
                                await db.SaveChangesAsync();
                            }
                            else
                            {
                                logger.LogWarning(
                                    "OIDC [{Scheme}]: DefaultTeamId {TeamId} no longer exists — " +
                                    "JIT user {Email} not added to a default team.", scheme, teamId, email);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex,
                                "OIDC [{Scheme}]: failed to add JIT user {Email} to default team {TeamId}.",
                                scheme, email, teamId);
                        }
                    }
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

                // A7/T1-13: refuse OIDC sign-in for administratively disabled
                // accounts (offboarded users must not re-enter via SSO).
                if (user.IsDisabled)
                {
                    logger.LogWarning(
                        "OIDC [{Scheme}]: refusing sign-in for disabled account {Email}.",
                        scheme, email);
                    context.Response.Redirect("/login?error=disabled");
                    context.HandleResponse();
                    return;
                }

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

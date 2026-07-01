# KrakenDeploy — Per-Account SSO (multi-account OIDC)

| | |
|---|---|
| **Version** | 0.1 |
| **Date** | 2026-06-30 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude Code |
| **Status** | `Draft` |
| **Technologies** | .NET 10, ASP.NET Core Authentication (OpenID Connect), EF Core 10, PostgreSQL |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Data`, `KrakenDeploy.Server.Core`, `KrakenDeploy.ControlPlane` |

## Purpose

Enable external OIDC single sign-on **per business account** in SaaS multi-account mode, where each account brings its **own** corporate IdP (Entra, Okta, ADFS, …) and its IdP configuration lives in that account's **own tenant database**. Single-instance behaviour is unchanged.

## Background — why it was gated off

In single-instance, [`OidcRegistrar.RegisterSchemes`](../src/KrakenDeploy.Server/Auth/OidcRegistrar.cs) runs **once at composition time**: it reads every enabled `IdentityProvider` row from the one `KrakenDb` and registers one `AddOpenIdConnect` scheme per provider (`oidc_{providerId:N}`, callback `/signin-oidc_{providerId:N}`), with an `OnTicketReceived` handler that provisions/links the user and issues the `KrakenDeploy.Auth` Identity cookie.

That model cannot hold in multi-account because ASP.NET Core authentication schemes are **process-global and registered at startup**, but per-account IdP config lives in per-account databases that are not even resolvable at composition time. So `RegisterSchemes` returns early when `MultiAccount:Enabled` — disabling SSO for all tenants. This document removes that gate.

## Decision — in-process dynamic per-tenant schemes (not a central broker)

The architecture sketch in `saas-multi-account-architecture.md` §9 proposed a **central auth broker** host (`auth.<base>`) behind one stable redirect URI. We chose the **in-process** approach instead, after reading the existing flow:

- The login page already enumerates the **current tenant's** providers from the tenant DB (`AccountResolutionMiddleware` resolves the account *before* the page renders).
- Challenge, IdP redirect, callback, OIDC correlation/nonce cookies, and the final host-only `KrakenDeploy.Auth` cookie all live on the **tenant's own subdomain** — no cross-host anything.
- The entire `OnTicketReceived` sign-in tail (provisioning, `(scheme,sub)` linking, group mapping, license-cap gate, `SignInManager.SignInAsync`) runs **unchanged**, because the callback lands on the tenant subdomain with the tenant DB already bound.

A central broker would break the third point: its callback runs on `auth.<base>` with **no resolved account** and **cannot** set the tenant host's host-only cookie, forcing a bespoke, security-critical signed cross-domain handoff token — and it would *still* read the tenant's IdP config. Its one benefit (a single stable redirect URI) is weak when each tenant configures their **own** IdP and naturally registers a redirect URI on their **own** subdomain. The broker remains the right call only if a first-party shared IdP is introduced later; this design does not preclude it.

## Design

Three moving parts, all **gated by `MultiAccount:Enabled`** (single-instance keeps the existing static registration verbatim).

### 1. Scheme naming

Multi-account scheme name encodes the account so options can be resolved to the right tenant DB:

```
oidc_{accountId:N}_{providerId:N}
```

`accountId` is the immutable catalog account id (not the subdomain), so the name and the IdP-registered redirect URI are stable across subdomain renames / white-label custom domains. Callback path mirrors the single-instance convention: `CallbackPath = /signin-{scheme}` = `/signin-oidc_{accountId:N}_{providerId:N}`. `OidcRegistrar.SchemeName(Guid accountId, Guid providerId)` is added alongside the existing single-provider overload.

### 2. Request-time scheme provider

A decorator over the framework `AuthenticationSchemeProvider`, registered as the singleton `IAuthenticationSchemeProvider` only in multi-account. It reads the current request's resolved account via `IHttpContextAccessor` → `HttpContext.Items["kd.account.resolved"]` (set by `AccountResolutionMiddleware`, which runs before `UseAuthentication`). It overrides only the OIDC-dynamic paths and delegates everything else to the inner provider:

- `GetSchemeAsync(name)` — if `name` matches the `oidc_{guid}_{guid}` pattern, parse `(accountId, providerId)`, confirm the provider is enabled for that account against a small per-account cache, and synthesize `new AuthenticationScheme(name, displayName, typeof(OpenIdConnectHandler))`. Unknown pairs are **not** synthesized (fail closed). Otherwise delegate.
- `GetRequestHandlerSchemesAsync()` — near-zero cost on non-callback requests: only when the request path starts with `/signin-oidc_` and an account is resolved does it parse the scheme from the path, confirm existence (cached), and return that one scheme; otherwise delegates to the inner provider.
- `GetAllSchemesAsync()` — inner schemes plus the resolved account's OIDC schemes (when an account is resolved).
- All `GetDefault*SchemeAsync`, `AddScheme`, `RemoveScheme`, `TryAddScheme` — delegated.

A per-account **provider-id cache** (`IMemoryCache`, keyed by `accountId`, short TTL + explicit eviction) backs the existence checks so the auth middleware never hits the tenant DB on the hot path.

### 3. Tenant-keyed options

A singleton `IConfigureNamedOptions<OpenIdConnectOptions>` (registered only in multi-account) configures options for any `oidc_{accountId}_{providerId}` name the framework's `IOptionsMonitor` resolves (it no-ops on other names). It opens a DI scope, resolves the account via `IAccountResolver.ResolveByIdAsync(accountId)`, opens a tenant `KrakenDbContext` under `IAccountContext.WithAccount(account)`, loads the `IdentityProvider`, decrypts the client secret (`AesEncryptionService`), and sets `Authority`/`ClientId`/`ClientSecret`/`ResponseType=code`/`UsePkce`/`SignInScheme=External`/`CallbackPath`/scopes/`Events` — reusing the **same** `OidcRegistrar.BuildEvents`. `IConfigureNamedOptions.Configure` is synchronous, so the resolve+DB read is sync-over-async; it runs **once per scheme** and is then cached by `IOptionsMonitor` (see eviction below).

The `OpenIdConnectHandler` and the framework `OpenIdConnectPostConfigureOptions` are registered once (via a sentinel template scheme that is never emitted by the login page nor challengeable — its options are never resolved, so its post-configure never runs), so dynamically-named schemes resolve a real handler + the standard backchannel/metadata/data-protection wiring.

### 4. Cache eviction on edit

Because options are cached per scheme name in the process-wide `IOptionsMonitor`, an admin editing an IdP must evict the stale entry — a farm restart per tenant edit is not acceptable. `IdentityProviderService` (multi-account) calls a small `IOidcSchemeCacheInvalidator` abstraction (Server.Core; no-op default registered by `AddKrakenDeployData`, real impl registered by Server in multi-account) on `Create`/`Update`/`Delete`, passing `(accountId, providerId)`. The real implementation evicts both the per-account provider-id cache entry and the `IOptionsMonitorCache<OpenIdConnectOptions>` entry for that scheme. (Single-instance keeps its existing "restart to apply" semantics — the static registration path is unchanged.)

### 5. Login page + challenge endpoint

[`Login.razor`](../src/KrakenDeploy.Server/Components/Account/Login.razor) builds the per-button challenge URL with the account-qualified scheme name in multi-account (`OidcRegistrar.SchemeName(accountId, providerId)`, using the resolved `IAccountContext.CurrentAccountId`); single-instance unchanged. The [`/login/external`](../src/KrakenDeploy.Server/Program.cs) endpoint adds a defense-in-depth check: the `accountId` parsed from the requested scheme must equal the current resolved account — so account B's login page can never initiate account A's IdP challenge (it would also fail at the correlation-cookie step, but we reject earlier and explicitly).

## Security model

- **Fail closed.** Unknown account/provider → scheme not synthesized → challenge returns `?error=unknown_provider`. Options configurator throws if the provider row is missing → handled as `OnRemoteFailure`. No fallback to a default IdP, ever.
- **Cross-account isolation.** The scheme name embeds the account; options load only that account's provider from that account's DB; the callback lands on the tenant subdomain so `OnTicketReceived` provisions/signs-in against the correct tenant DB. A challenge cross-initiated from another account's subdomain is rejected by the `/login/external` account check and, failing that, by OIDC correlation (the correlation cookie is host-only and the IdP's redirect URI points at the owning tenant's host).
- **Secret handling.** Client secrets stay encrypted at rest (`ClientSecretEncrypted`, AES master key); decrypted only in-memory during options configuration. No secret is logged. (GDPR / public-sector: IdP secrets and user identifiers never leave the tenant boundary.)
- **Existing gates preserved.** `email_verified=false` refusal, service-account SSO block, auto-provision flag, license-cap gate, `(scheme,sub)` linking — all unchanged (same `BuildEvents`).

## Rollback / kill-switch

- The per-account feature flag **`security.allow-oidc-sign-in`** (evaluated from the tenant DB, checked in both `Login.razor` and `/login/external`) is the **live** kill-switch: a tenant or operator can disable OIDC instantly, no deploy. Local-account login (break-glass admin) always works.
- Full rollback = redeploy without the multi-account registration; the `MultiAccount:Enabled` gate means single-instance is never affected by this change.

## Out of scope

- LDAP / SAML / ADFS provider *types* (entity carries them; only OIDC is implemented — same as single-instance).
- A control-plane UI for per-tenant IdPs — each tenant manages its own via the existing in-app IdP admin page (which now functions per-tenant).
- A central first-party auth broker (the §9 design) — deferred; not precluded.

## Open questions / risks

- **Sync-over-async** in `IConfigureNamedOptions.Configure` (one resolve + one DB read per scheme, then cached). Acceptable and bounded, but is the one place to watch under load spikes (many distinct first-time schemes at once).
- **Sentinel template scheme** vs. manual handler+post-configure registration — pick whichever builds clean and leaves no challengeable bogus scheme; verify the template's options are never resolved.
- **White-label custom domains** — callback lands on the custom domain (tenant controls the IdP redirect URI there); confirm the on-demand-TLS edge path passes Host through (it does, per the SaaS Caddyfile).

## References

- `src/KrakenDeploy.Server/Auth/OidcRegistrar.cs` — single-instance registration + `BuildEvents` (reused)
- `src/KrakenDeploy.Server/Accounts/HttpAccountContext.cs` — `Items["kd.account.resolved"]` integration point
- `src/KrakenDeploy.Server.Core/Domain/Security/IdentityProvider.cs` — per-tenant IdP config entity
- `src/KrakenDeploy.Server.Data/Services/IdentityProviderService.cs` — CRUD + eviction hook
- `docs/saas-multi-account-architecture.md` §9 — the central-broker alternative (superseded by this doc for now)
- `docs/oidc-templates/*.md` — per-IdP setup guides (redirect-URI note needs the multi-account callback path)

## History

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-30 | Domagoj Jugović | Initial design: in-process dynamic per-tenant OIDC schemes (request-time scheme provider + tenant-keyed options + eviction-on-edit); chosen over the §9 central broker with rationale. |

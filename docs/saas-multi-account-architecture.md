# KrakenDeploy — SaaS Multi-Account Architecture

| | |
|---|---|
| **Version** | 0.4 |
| **Date** | 2026-06-16 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude Code |
| **Status** | `Draft` |
| **Technologies** | .NET 10, Blazor Server, ASP.NET Core, EF Core 10, PostgreSQL, Hangfire, Caddy |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Data`, `KrakenDeploy.Server.Core` + new control-plane components (`KrakenDeploy.ControlPlane`*, name TBD) |

\* *Working name for the catalog + provisioning + signup-portal surface. Could also live as a module inside `KrakenDeploy.Server` keyed by host.*

## Revision history

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-16 | DJ | Initial draft: account-as-tenant, subdomain identification, database-per-account with catalog routing, self-service provisioning portal. |
| 0.2 | 2026-06-16 | DJ | DNS/TLS §11 rewritten for the `krakendeploy.com` zone: wildcard DNS record (catch-all, not apex-fallback) resolves all account subdomains; wildcard TLS cert via DNS-01; per-account DNS automation reserved for dedicated-infra/custom-domain cases; on-demand TLS scoped to custom domains only. |
| 0.3 | 2026-06-16 | DJ | Domain normalized to `krakendeploy.com` throughout. Adopted patterns from the Octopus Cloud signup flow: two identity planes (control-plane owner/ops vs isolated per-instance app users); region-at-signup → shard placement (residency); warm-pool provisioning optimization; per-account maintenance window for staggered fleet migrations; per-account IP allow-list with a platform carve-out; consent capture at signup. New open questions: customer-facing Control Center, dedicated tenant app domain, warm pool, subdomain rename. |
| 0.4 | 2026-06-16 | DJ | Identity model refined to **three** planes (Octopus `id.octopus.com` IdP / `billing` Control Center / `*.octopus.app` instances): a central identity/SSO authority distinct from control-plane users+roles and per-instance app users. Added entitlements/quotas as a control-plane concept enforced in-instance, plus control-plane users+roles. Clarified platform IdP (optional, defer, self-host in-region) vs per-account customer SSO (priority for public-sector). New open question: central platform IdP. |

---

## 1. Purpose & scope

KrakenDeploy today is a single-instance product: one deployment, one database, one set of users, multiple **Spaces** inside it (Spaces are the *within-customer* boundary, carried in the URL as `/s/{slug}` — the space-in-URL routing work). This document designs the layer **above** Spaces needed to run KrakenDeploy as a **SaaS** for many independent customers ("business accounts") from a shared codebase, with a self-service portal that provisions a new account end-to-end.

A **business account** is a fully isolated instance of KrakenDeploy — its own users, its own Spaces, its own data — that *mimics a standalone install*. Accounts never share users or data. The boundary between two accounts is a **cross-customer** boundary: a leak is a reportable GDPR incident, not a cosmetic glitch. This is a strictly higher bar than the Space boundary.

Out of scope for v0.1: billing/metering, the legacy on-prem single-install topology (unchanged — see [`on-prem-guide.md`](on-prem-guide.md)), and cross-account analytics (explicitly not a goal).

## 2. Goals / non-goals

**Goals**
- One stateless app tier serving all accounts; the active account is resolved per request from the **subdomain**.
- **Database-per-account** isolation by default, with a **catalog** mapping account → connection string, so density and isolation are a per-account routing decision (many small DBs on a shared server → one dedicated DB → one dedicated server/deployment).
- A **self-service portal** that provisions a new account: validate subdomain → provision database → run migrations + seed → register the connection-string mapping in the catalog → (optionally) create the DNS record → create the first admin.
- Reuse the existing Space machinery unchanged inside an account.
- Build the catalog + resolution **in-house** (no third-party multi-tenancy framework) for full control over the isolation core.

**Non-goals**
- Users belonging to multiple accounts. Membership is **one user ↔ one account**. A consultant serving two accounts has two separate user records (even with the same email / external identity). This is a deliberate simplification (see §9).
- Row-level multi-tenancy as the *default*. Supported as a density tier, but not the baseline (see §6).

## 3. Glossary

| Term | Meaning |
|---|---|
| **Business account** (account) | Top-level tenant. One isolated KrakenDeploy instance: own users, Spaces, data. Identified by a subdomain. |
| **Space** | Existing *within-account* boundary (`/s/{slug}`). Unchanged by this design. |
| **Tenant database** | A PostgreSQL database holding one (default) or more (density tier) accounts' data — the existing KrakenDeploy schema. |
| **Catalog** (control plane) | Small central database mapping subdomain → account → connection string + account status/tier. Holds routing metadata only; no customer PII. |
| **Control plane** | Catalog + provisioning service + signup/management portal. Runs on non-account hosts. |
| **Data plane** | The app tier serving account subdomains + the tenant databases. |
| **Shard** | A PostgreSQL server/instance that hosts one or more tenant databases. |

## 4. Decisions

Locked for this draft (open ones in §17):

- **D1 — Account is the top tenant boundary; Spaces nest inside it.** `Account → (Users, Spaces, …) → Space-scoped resources`.
- **D2 — Subdomain identifies the account** (`acme.krakendeploy.com`). Spaces stay in the path beneath it (`acme.krakendeploy.com/s/{slug}`). Rationale: the browser scopes cookies per host (RFC 6265), so a host-only / `__Host-` session cookie is isolated per account *for free* — no shared global "active account" value to flip across tabs (the failure mode that killed the global Space cookie), and the cookie/TLS/CSP boundary lines up with the tenant boundary.
- **D3 — Database-per-account by default**, with the catalog supporting the full density spectrum. The connection string is the isolation boundary; this eliminates the row-level cross-customer leak class entirely for the default tier. Row-level sharing (multiple accounts per DB + `account_id` discriminator + RLS) is an *optional* density tier, not the baseline.
- **D4 — Users isolated per account** (one user ↔ one account). No shared identities, no account switcher, no cross-account membership table.
- **D5 — Build catalog + resolution in-house.** No Finbuckle/ABP. The resolution pattern is the same shape as the existing `ISpaceContext` one level down; the hard parts (catalog, connection routing, provisioning, migration fan-out) are custom regardless. See §15.
- **D6 — Self-service provisioning via a control-plane portal**, executed as an idempotent, compensating async workflow (§10), behind abuse guardrails (§14).

## 5. Architecture

```
                          Internet
                             │
                    ┌────────┴─────────┐
                    │      Caddy       │  wildcard TLS (*.krakendeploy.com);
                    │  (reverse proxy) │  on-demand TLS only for custom domains
                    └───┬──────────┬───┘
        acme.krakendeploy.com  │          │  app.krakendeploy.com / signup.krakendeploy.com
        globex.krakendeploy.com│          │  auth.krakendeploy.com
                        ▼          ▼
              ┌───────────────┐  ┌──────────────────────────┐
              │  App tier     │  │  Control plane           │
              │ (stateless,   │  │  - Signup/mgmt PORTAL    │
              │  HA, resolves │  │  - Provisioning service  │
              │  account per  │  │  - Central OIDC callback │
              │  request)     │  └────────────┬─────────────┘
              └──────┬────────┘               │
        resolve host │ → catalog → conn str   │ writes
                     │                        ▼
                     │                 ┌──────────────┐
                     │                 │  CATALOG DB  │  subdomain → account
                     │   reads (cached)│  (control)   │  → connection string
                     │ ◄───────────────┤              │  (routing only, no PII)
                     ▼                 └──────────────┘
        ┌──────────────────────────────────────────────┐
        │              Tenant databases                 │
        │  ┌─────────┐  ┌─────────┐        ┌─────────┐  │
        │  │ acme DB │  │globex DB│  …     │ big-cust│  │
        │  └─────────┘  └─────────┘        │ DB (own │  │
        │   (shared Postgres server[s])    │ server) │  │
        │                                  └─────────┘  │
        └──────────────────────────────────────────────┘
```

The **app tier** is one deployment (scaled for HA exactly as [`ha-pair.md`](ha-pair.md) describes), stateless, sharing DataProtection keys. It does not "know" accounts at build time — it resolves the account from the `Host` header on every request.

## 6. Tenancy & isolation model

**Account is the database boundary.** In the default tier each account is its own PostgreSQL database holding the *existing* KrakenDeploy schema (Spaces, users, projects, …) provisioned fresh. There is no `account_id` discriminator on tenant rows in this tier — the database you connected to *is* the account. Spaces work unchanged inside it.

**The density spectrum (chosen per account, via the catalog):**

| Tier | Isolation | When |
|---|---|---|
| Many DBs on a shared Postgres server | Database-per-account (no row mixing) | **Default.** Cost-efficient at dozens–hundreds of accounts. |
| Dedicated database on a dedicated server | Physical | Large/high-load account. |
| Dedicated deployment (app + DB + infra) | Full silo | Account contractually requiring isolated infra / residency. |
| Multiple accounts in one DB (`account_id` + query filter + RLS) | Row-level | Optional extreme-density tier — **avoid unless a tier demands it** (reintroduces the cross-customer row-leak class). |

The catalog makes promotion between tiers a routing change (move the database, update the connection string), not an application rewrite.

**If — and only if — the row-level tier is used**, the entity model gains `account_id` on every account-scoped table, an EF Core 10 *named* global query filter (`e.AccountId == CurrentAccountId`, stacked above the Space filter), write-side stamping with throw-on-mismatch, and **PostgreSQL Row-Level Security** as a database-enforced backstop (`ENABLE` + `FORCE ROW LEVEL SECURITY`, app connects as a non-owner / non-`BYPASSRLS` role, `current_setting('app.current_account')` set via `SET LOCAL` inside the request transaction). The default DB-per-account tier needs none of this.

## 7. Request resolution

Resolution is the cross-customer authorization boundary and must **fail closed**.

```
Host: acme.krakendeploy.com
  → AccountResolutionMiddleware
      slug = subdomain label ("acme")
      account = Catalog.GetBySubdomain(slug)   // cached; null/Disabled → 404 landing, never a default DB
      AccountContext.Set(account)              // ambient, circuit-/request-scoped
      DbContextFactory builds context against account.ConnectionString
  → existing Space resolution (/s/{slug}) runs unchanged within the account
```

Illustrative shape (mirrors the existing `ISpaceContext` pattern one level up):

```csharp
public interface IAccountContext
{
    AccountId AccountId { get; }            // resolved, validated; throws if unresolved
    string Subdomain { get; }
    string ConnectionStringRef { get; }     // a NAME resolved to a secret, not the raw secret
}

// Built before any DbContext. No DbContext / no query without a resolved account.
public interface IAccountResolver
{
    Task<ResolvedAccount?> ResolveAsync(string host, CancellationToken ct);
}
```

The `DbContextFactory` becomes account-aware: it reads `IAccountContext`, resolves the connection string (from a secrets store via the catalog reference, cached), and opens the context against the right database. Because the connection varies per account, **DbContext pooling is not used** for tenant data (Microsoft EF Core multitenancy guidance) — KrakenDeploy already uses a Scoped `IDbContextFactory`, so this fits; do not switch to pooling.

**Background work** (Hangfire) must run inside an explicit resolved account context or be denied — never an ambient default. Today's workers run "Default-Space only"; under SaaS each job carries its account (and thus its tenant DB connection) or fails closed.

## 8. Catalog (control-plane database)

Small, central, always-available, heavily cached. Routing metadata only — **no customer PII** (keeps data residency clean and limits the blast radius of the one shared DB).

```sql
-- Illustrative; final shapes via EF migration in the control-plane project.
CREATE TABLE business_accounts (
    id              uuid PRIMARY KEY,
    subdomain       text NOT NULL UNIQUE,          -- normalized, lower-case, validated
    display_name    text NOT NULL,
    status          text NOT NULL,                 -- Provisioning | Active | Suspended | Deprovisioning
    tier            text NOT NULL,                 -- Shared | DedicatedDb | DedicatedServer | DedicatedDeployment
    shard_id        uuid NOT NULL REFERENCES shards(id),
    conn_secret_ref text NOT NULL,                 -- reference into the secrets store, NOT the raw string
    created_utc     timestamptz NOT NULL,
    modified_utc    timestamptz NOT NULL
);

CREATE TABLE shards (                              -- a Postgres server/instance with capacity
    id              uuid PRIMARY KEY,
    name            text NOT NULL,
    host_secret_ref text NOT NULL,                 -- admin/connection secret reference
    capacity        int  NOT NULL,                 -- soft cap on accounts per shard
    status          text NOT NULL                  -- Online | Draining | Offline
);
```

Connection secrets live in a secrets store (the platform's existing mechanism — Windows DPAPI/keystore or a vault), and the catalog stores only a *reference*. The catalog is read on every request, so it is cached per app instance with explicit invalidation on account/shard change.

**Entitlements / quotas** are a control-plane concept tied to the account's `tier` — Octopus surfaces these on the subscription (Machines 10 / Projects 10 / Tenants 10 / Users 10 / Spaces 1 / Task cap 5). Store the caps on the account (or a `tiers` table the account references: machines, projects, tenants, users, Spaces, task cap, storage); the **instance reads its entitlements from the catalog (cached) and enforces the caps in-app**. This is also the natural hook for metering/billing later (out of scope for v0.x). The control plane additionally has its **own users + roles** (Subscription Owner / operator), distinct from per-instance app users — keep these in the control-plane DB, not a tenant DB.

## 9. Identity, users & OIDC

Because users are isolated per account (**D4**) and the default tier is database-per-account, identity is *simpler*, not harder:

- Each account database has its own `AspNetUsers` / `AspNetUserLogins`. The same person's external identity (e.g., one Google `sub`) maps to a **separate user row in each account's database** — no uniqueness collision, no composite primary keys, no custom Identity store. **The `businessAccountID`-in-PK idea is unnecessary in this tier** — separate databases give separate user tables for free. (Composite uniqueness `(account_id, normalized_email)` / `(account_id, provider, providerKey)` is needed *only* in the row-level density tier.)
- **Per-account IdP config:** the existing `IdentityProviders` table lives inside each tenant DB — every account brings its own Google/Entra/ADFS, or uses a platform default. See [`docs/oidc-templates`](oidc-templates).
- **The one OIDC wrinkle — redirect URIs.** OIDC redirect URIs are per-host (`https://acme.krakendeploy.com/signin-oidc`), and most IdPs reject wildcard redirect URIs. Resolution: a **central auth-callback domain** (`auth.krakendeploy.com/signin-oidc`) registered once with the IdP. It completes the OIDC dance, identifies the target account (from the `state`/return URL), provisions/signs in the user **inside that account's DB**, and drops the user onto their account subdomain with a host-only (`__Host-`) session cookie. This is the standard "central identity + per-tenant session" pattern.

```
acme.krakendeploy.com/login  →  auth.krakendeploy.com (one registered redirect URI)
   → IdP  → auth.krakendeploy.com/signin-oidc  → resolve account from state
   → sign in user in acme's tenant DB  → 302 acme.krakendeploy.com (host-only session)
```

### Identity planes (informed by Octopus Cloud)

Octopus separates **three** planes, and the split is worth copying:

1. **Identity plane** (`id.octopus.com`) — a central sign-in / account service: one global person identity (name, email, password, MFA) + connected social logins (Google/Microsoft/GitHub). Both the Control Center and the instances trust it, so a person signs in once; it is the single OAuth/OIDC authorization server (one registered redirect URI). The Control Center's "Profile" link goes here, with a link back.
2. **Control plane** (`billing.octopus.com`) — subscriptions, billing, **entitlements/quotas**, its OWN control-center users + roles (e.g. "Cloud Subscription Owner", invite up to N), and instance provisioning / config. A relying party on the identity plane.
3. **Data plane** (`*.octopus.app`) — the product; **per-instance app users**, isolated per account (your **D4**).

One identity-plane account can be a control-plane Subscription Owner of several accounts *and* a separate app user inside each instance — one global identity, isolated per-plane user records. That is exactly your "one consultant, two accounts, two separate users" outcome. A LAUS-internal admin console (manage all tenants) is needed regardless; a *customer-facing* multi-account Control Center is optional (see §17).

**For KrakenDeploy — two different OIDC concepts, don't conflate:**

- **Per-account customer SSO (priority for public-sector):** each institution federates its OWN Entra/ADFS to ITS instance, so employees sign in with their corporate identity and that PII stays at the institution. This is the per-tenant `IdentityProviders` model described above, and it matters most for the target audience.
- **Central platform IdP (`id.krakendeploy.com`) — optional, defer.** It buys cross-plane SSO + a social-login hub + a single redirect target, but it concentrates platform-wide identity PII into one high-value store. Not needed for v1: the Control Center can authenticate against the control-plane DB, and the per-subdomain redirect-URI problem is already solved by the central **auth-callback domain** described above without a full IdP. If built later, self-host it in-region (Duende IdentityServer / Keycloak) — do not put public-sector identities at a third party.

## 10. Self-service portal & provisioning

A control-plane **portal** (`signup.krakendeploy.com` / `app.krakendeploy.com`, a non-account host) lets a prospective customer create an account. Provisioning is a **single orchestrated, idempotent, compensating workflow** run as a Hangfire job — never an inline request, because it touches DNS + a new database + migrations + the catalog and may take seconds to minutes.

**Signup inputs (captured up front).** Organization/display name; desired **subdomain** (with a live availability check, à la Octopus's "`x.octopus.app` is available"); **region** (drives shard placement = data residency, §13); and **terms acceptance** (consent record — version + timestamp, §14). The signing-up owner becomes the account's first admin.

### Provisioning state machine

```
Requested
  → SubdomainValidated     (format, reserved-word blocklist, uniqueness, abuse checks)
  → ShardSelected          (pick a shard in the chosen region with capacity, or allocate dedicated)
  → DatabaseProvisioned    (CREATE DATABASE on the shard)
  → SchemaMigrated         (apply EF migrations to the new DB)
  → Seeded                 (default Space, built-in roles, settings — existing seed path)
  → CatalogRegistered      (business_accounts row + conn secret stored; status=Provisioning)
  → DnsConfigured          (create record if not covered by wildcard — see §11)
  → AdminInvited           (first admin user created in the tenant DB; invite/initial credential)
  → Active                 (status flips; subdomain serves the app)
  ─ any step fails ─►      Failed → COMPENSATE (tear down in reverse) → cleaned
```

Each step is **idempotent** (safe to retry) and has a **compensation** (drop DB, delete DNS record, remove catalog row, revoke secret) so a partial failure never leaves an orphaned database, a dangling DNS record, or a half-registered account. The portal shows live status (`Provisioning…` / `Ready` / `Failed`) by polling the job — the customer is not blocked on a synchronous request.

**Provisioning latency / warm pool (optimization).** Cold `CREATE DATABASE` + migrate + seed takes many seconds. Octopus's "ready in under a minute" implies pre-built instances. To match it, keep a small **warm pool** of pre-created, pre-migrated, empty tenant DBs per shard; signup then *claims + brands* one (near-instant) and the pool refills in the background. Trade-off: a few idle DBs per shard. Optional — the saga works without it, just slower.

Illustrative provisioner surface (each dependency is pluggable + independently testable):

```csharp
public interface IAccountProvisioner            // orchestrates the saga below
{
    Task<ProvisioningResult> ProvisionAsync(NewAccountRequest req, CancellationToken ct);
}

public interface IDatabaseProvisioner           // CREATE DATABASE + migrate + seed; DROP on compensate
public interface IDnsProvisioner                // create/delete a record; no-op when wildcard covers it (§11)
public interface ICatalogStore                  // register/lookup/remove account + shard selection
public interface ISecretStore                   // store/resolve/revoke the per-account connection secret
```

### Abuse & cost guardrails (mandatory — see §14)

Open self-service that auto-provisions databases and DNS is a real cost/DoS vector (a script that creates 10 000 accounts creates 10 000 databases). Required controls, configurable per environment:

- **Verified email** before any provisioning starts.
- **Approval / quota gate:** a configurable mode — fully open, admin-approved, or invite-only. For RH public-sector go-live, default to **admin-approved or invite-only**, not open signup.
- **Rate limits** on the signup endpoint and a per-source provisioning quota.
- **Reserved-subdomain blocklist** (`www`, `api`, `auth`, `app`, `signup`, `admin`, `static`, `_*`, …) and a strict slug format; never reuse a released subdomain identifier (subdomain-takeover / stale-DNS hazard).

## 11. DNS & TLS strategy

All account subdomains point at the **same** app tier (which resolves the account from the `Host` header), so a single wildcard covers DNS *and* TLS with no per-account records. Concrete domain: `krakendeploy.com`, accounts at `acc1.krakendeploy.com`, `acme.krakendeploy.com`, …

- **DNS — wildcard record (recommended).** One record `*.krakendeploy.com → <app-tier load balancer>` (A to the LB IP, or CNAME to a stable LB hostname) resolves *every* single-label account subdomain without a per-account record (RFC 4592). Important distinction: a wildcard is a **catch-all record**, not a "fall back to the apex" — DNS does **not** resolve `accN.krakendeploy.com` from a `krakendeploy.com` (apex) record, which is correct and expected; the `*` record is the mechanism that resolves the subdomains. Caveats: the wildcard matches **one label only** (`a.b.krakendeploy.com` would need `*.b.krakendeploy.com`); the apex (`krakendeploy.com`) still needs its own record; and any **explicit** record (`www`, `auth`, `app`, `signup`) **overrides** the wildcard — keep the reserved hosts explicit. With a wildcard in place, the portal's `DnsConfigured` step is a no-op. Requires that the DNS provider for the zone supports a wildcard record (most do — Cloudflare, Azure DNS, Route 53, common registrars; confirm for `krakendeploy.com`).

- **DNS — explicit per-account records (only when needed).** Required only when (a) the provider can't serve a wildcard for this zone, or (b) a specific account must point at **different** infrastructure (a dedicated-deployment account). Then the portal's `DnsConfigured` step calls the provider API via `IDnsProvisioner` to create `accN.krakendeploy.com` — a **CNAME to the stable app-tier hostname** is safer than a hard-coded A/IP — compensated by deletion on teardown. Gotchas: least-privilege zone-write credentials, propagation/TTL (use a low TTL; provisioning may briefly show "DNS pending"), idempotency (don't duplicate records), and never reuse a released subdomain (takeover hazard).

- **TLS — wildcard certificate (recommended).** One `*.krakendeploy.com` certificate covers every account subdomain: no per-account issuance, no ask-endpoint, simplest path. A wildcard cert must be obtained via the **DNS-01** ACME challenge (HTTP-01 cannot issue wildcards) — Caddy ([`deploy/caddy`](../deploy/caddy)) supports DNS-01 with the matching DNS-provider plugin, or install + renew the cert manually.

- **On-demand TLS** (Caddy, gated by a catalog **`ask`** endpoint that checks "is this a known, active account subdomain?") is needed **only for custom domains** (`deploy.acme.hr`, white-label tier), where a wildcard cert can't apply — issue per-host certs on first request, gated so certs are minted only for real accounts. Custom domains also need TXT/CNAME ownership validation, host-header routing, and a delete-CNAME-before-offboard rule.

**Net for the default tier: one wildcard DNS record + one wildcard certificate, zero per-account DNS/TLS operations.** Per-account record automation (`IDnsProvisioner`) is reserved for the dedicated-infra and custom-domain cases.

## 12. Fleet migrations

Database-per-account moves correctness risk into **fleet operations**: every schema change must apply to every tenant DB. Required: an **idempotent migration orchestrator** that enumerates accounts from the catalog, applies pending EF migrations to each distinct connection, tracks per-DB migration state, and **fails loudly on drift** (a DB that missed a migration must surface, not silently diverge). Provisioning a *new* account applies the full migration set as part of `SchemaMigrated`. Align the orchestrator with the existing `dotnet-ef` setup (startup-project = the Data project, `--framework net10.0`). This is the single largest operational cost of the chosen model and must be built early (Phase 2).

**Per-account maintenance windows** (an Octopus-style setting stored on each account) let the orchestrator **stagger** rollouts — apply each account's pending migrations inside *its* nominated window rather than all at once. Batches by window; surfaces any account that fell behind.

## 13. Backup, restore, erasure, residency

Database-per-account makes per-customer data lifecycle natural:
- **Backup/restore** per account (per-database).
- **Right-to-erasure** = drop the account's database (clean, complete).
- **Data residency / customer-managed keys** = place the account's database on a shard in the required region / with the required key — a catalog/shard decision, no app change.

## 14. Security considerations

- **Cross-customer boundary.** The account boundary is stronger than the Space boundary; treat resolution as security-critical and fail closed (unknown/disabled subdomain → no DB, no default).
- **Never trust the host alone for data.** The subdomain selects the candidate; the resolved connection string is the boundary. In the row-level tier additionally validate `account_id` at every layer and back it with RLS.
- **Secrets.** Connection strings are secret-store references in the catalog; the app role per tenant DB is least-privilege (and non-owner / non-`BYPASSRLS` if RLS is in play).
- **Provisioning abuse.** See §10 guardrails — for public-sector go-live, do **not** ship fully-open signup that auto-provisions infrastructure.
- **Per-account IP allow-list (optional, public-sector fit).** An account may restrict its app to specific IP ranges (the institution's networks). Always keep a **platform / trusted-services carve-out** so migrations, health checks, and support are never locked out — Octopus does exactly this ("Trusted Octopus Services always allowed"). Off by default.
- **Consent record.** Capture terms / privacy / acceptable-use acceptance at signup (version + timestamp) as a compliance artifact.
- **Isolation tests (must-have).** Wrong-subdomain → no access; a context-less Hangfire job is denied (not defaulted); provisioning compensation leaves no orphaned DB/DNS/secret; (row-level tier only) RLS denies cross-account rows even with the app filter removed, and a pooled-connection context-bleed test.
- **GDPR.** Per-account erasure + residency satisfied by the silo path; the catalog deliberately holds no PII.

## 15. Affected components / blast radius (existing codebase)

- **New: control plane** — catalog DB + EF model, `IAccountResolver` / `IAccountContext`, account-resolution middleware, `IAccountProvisioner` + step providers, the signup/management portal, the central OIDC callback host, the migration orchestrator.
- **`KrakenDeploy.Server.Data`** — `DbContextFactory` becomes account-aware (connection from `IAccountContext`); migration tooling extended for fleet apply; (row-level tier only) named query filter + RLS.
- **`KrakenDeploy.Server`** — host-based pipeline split (account host vs control-plane host); host-only `__Host-` session cookie; Hangfire jobs carry account context; the existing Space resolution (`SpaceScopedComponentBase`, `SpaceUrlRedirectMiddleware`, `ISpaceContext`) is **unchanged** and runs inside the account.
- **Auth** — OIDC callback centralized to `auth.krakendeploy.com`; per-account IdP config already exists in-DB.

The recently completed **Space-in-URL** work is preserved as-is; the account layer sits above it.

## 16. Phased delivery

1. **Catalog + resolution** — catalog DB, host→account middleware (cached, fail-closed), account-aware `DbContextFactory`. Spaces/users unchanged inside.
2. **Provisioning + migration orchestrator** — the saga (§10) and the fleet migrator (§12). Load-bearing; build early.
3. **Subdomain + host-only session** — wildcard DNS + wildcard TLS cert (DNS-01); `__Host-` session cookie; verify reverse-proxy `Host`/`X-Forwarded-Host`. (On-demand TLS reserved for custom domains.)
4. **Self-service portal** — signup UX + status polling + abuse guardrails (start in admin-approved mode).
5. **OIDC via central auth domain** — one registered redirect URI; per-account IdP in-DB.
6. **Isolation + provisioning tests** (§14), including compensation/rollback.
7. **Dedicated/silo promotion + de-provisioning** — catalog-driven move to dedicated DB/server; drop-DB erasure; custom-domain support (later).

## 17. Open questions

- **Signup gating** — fully open vs admin-approved vs invite-only at go-live? (Recommended: not open for public-sector.)
- **Shard placement policy** — how is the target shard chosen (capacity, region, tier)? Manual vs automatic?
- **Subdomain namespace** — globally unique, first-come; reservation/rename policy; reserved list owner.
- **DNS provider** for the public `krakendeploy.com` zone (drives `IDnsProvisioner`), and whether wildcard is acceptable to the zone owner.
- **Control-plane home** — separate project (`KrakenDeploy.ControlPlane`) vs a host-keyed module inside `KrakenDeploy.Server`.
- **Migration rollout safety** — online vs maintenance-window fleet migrations; per-DB failure handling/alerting.
- **Customer-facing Control Center?** — give owners an Octopus-style multi-account console (manage several accounts, billing) or keep each account a standalone signup with no cross-account owner view? (Adds owner PII to the control plane.)
- **Tenant app domain** — subdomains of `krakendeploy.com` (with host-only cookies) vs a *dedicated* app domain (e.g. `*.krakendeploy.app`) for stricter origin isolation and to avoid reserving control-plane names in the tenant namespace.
- **Warm pool** — maintain pre-provisioned, pre-migrated tenant DBs for near-instant signup, or cold-provision per signup?
- **Subdomain rename** — allow changing an account's subdomain later (Octopus "Change URL"), with old-link/redirect-URI handling — or treat it as immutable?
- **Central platform IdP** — build `id.krakendeploy.com` (cross-plane SSO + social logins + single redirect target) or keep auth per-plane (control-plane DB) + per-account customer SSO + the central auth-callback domain? If built: self-hosted in-region (Duende IdentityServer / Keycloak) vs a cloud IdP (residency/processor concern for public-sector). Recommendation: defer; prioritize per-account customer SSO.

## 18. References

- KrakenDeploy: [`architecture.md`](architecture.md), [`ha-pair.md`](ha-pair.md), [`on-prem-guide.md`](on-prem-guide.md), [`deploy/caddy`](../deploy/caddy), [`docs/oidc-templates`](oidc-templates).
- EF Core multitenancy — https://learn.microsoft.com/ef/core/miscellaneous/multitenancy
- EF Core query filters (named filters, EF 10) — https://learn.microsoft.com/ef/core/querying/filters
- Azure Architecture Center, multitenancy tenancy models — https://learn.microsoft.com/azure/architecture/guide/multitenant/considerations/tenancy-models
- Azure Architecture Center, tenant domain names — https://learn.microsoft.com/azure/architecture/guide/multitenant/considerations/domain-names
- Azure Architecture Center, multitenant identity — https://learn.microsoft.com/azure/architecture/guide/multitenant/approaches/identity
- PostgreSQL Row-Level Security — https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- RFC 6265 (cookie host scoping) — https://datatracker.ietf.org/doc/html/rfc6265#section-4.1.2.3 ; MDN Set-Cookie — https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Set-Cookie
- Caddy on-demand TLS — https://caddyserver.com/docs/automatic-https#on-demand-tls
- Finbuckle.MultiTenant (evaluated, not adopted) — https://www.finbuckle.com/MultiTenant

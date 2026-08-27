# KrakenDeploy — SaaS Multi-Account Architecture

| | |
|---|---|
| **Version** | 0.7 |
| **Date** | 2026-06-24 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude Code |
| **Status** | `Draft` |
| **Technologies** | .NET 10, Blazor Server, ASP.NET Core, EF Core 10, PostgreSQL, Hangfire, Caddy, YARP |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Data`, `KrakenDeploy.Server.Core` + new control-plane components (`KrakenDeploy.ControlPlane`*, name TBD) |

\* *Working name for the catalog + provisioning + signup-portal surface. Could also live as a module inside `KrakenDeploy.Server` keyed by host.*

## Revision history

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-16 | DJ | Initial draft: account-as-tenant, subdomain identification, database-per-account with catalog routing, self-service provisioning portal. |
| 0.2 | 2026-06-16 | DJ | DNS/TLS §11 rewritten for the `krakendeploy.com` zone: wildcard DNS record (catch-all, not apex-fallback) resolves all account subdomains; wildcard TLS cert via DNS-01; per-account DNS automation reserved for dedicated-infra/custom-domain cases; on-demand TLS scoped to custom domains only. |
| 0.3 | 2026-06-16 | DJ | Domain normalized to `krakendeploy.com` throughout. Adopted patterns from the Octopus Cloud signup flow: two identity planes (control-plane owner/ops vs isolated per-instance app users); region-at-signup → shard placement (residency); warm-pool provisioning optimization; per-account maintenance window for staggered fleet migrations; per-account IP allow-list with a platform carve-out; consent capture at signup. New open questions: customer-facing Control Center, dedicated tenant app domain, warm pool, subdomain rename. |
| 0.4 | 2026-06-16 | DJ | Identity model refined to **three** planes (Octopus `id.octopus.com` IdP / `billing` Control Center / `*.octopus.app` instances): a central identity/SSO authority distinct from control-plane users+roles and per-instance app users. Added entitlements/quotas as a control-plane concept enforced in-instance, plus control-plane users+roles. Clarified platform IdP (optional, defer, self-host in-region) vs per-account customer SSO (priority for public-sector). New open question: central platform IdP. |
| 0.5 | 2026-06-17 | DJ | Added **§13 Self-upgrade, rolling deploy & drain (DigitalOcean)**: two-class state model (recoverable Blazor circuits vs irreplaceable in-flight deployments); DO-native rolling-upgrade runbook (Container Registry → expand migrations → node-by-node drain behind DO LB / Caddy → separate executor drain → contract); durable, resumable, lease-claimed step ledger in our own schema; expand/contract migration discipline extending §12 (forced by the shared single-binary app tier); Data Protection key ring + `IArtifactStore` on DO Spaces. Existing §13–§18 renumbered to §14–§19. Companion **[`self-upgrade-ha.md`]** split out for self-managed multi-node and plain-VM cloud HA (you operate the LB, Postgres HA, and rollout). |
| 0.7 | 2026-06-24 | DJ | Folded in the **agent-transport account-identity** decision (new companion **[`design-agent-transport-account-awareness.md`]**, v0.2): the agent plane resolves account **host-derived** — agents connect to their account subdomain and the server resolves from `Request.Host` via the same `AccountResolutionMiddleware`, reusing the §11 wildcard, **no new cross-tenant table** (an earlier `agent_registry` proposal was reversed). Added the agent plane to **§7**, agent control-plane traffic to **§11**, and the **`KrakenDeploy.Server.Transport`** tier to **§16**. Two verified standalone bugs noted (enrollment `IgnoreQueryFilters`; `AgentHub` fail-open). |
| 0.6 | 2026-06-23 | DJ | Reframed **§13** around the **blue-green slot deployment** scheme (new companion **[`blue-green-slot-deployment.md`]**): three fixed slots per app node, releases rotate round-robin, an opaque-`release_id` `__Host-kd_ver` version-pin cookie, a per-node **YARP** router owning release→slot routing while **Caddy** stays a version-agnostic HA edge (TLS + WebSocket + node affinity, **no DB**), and default-flip cutover with natural drain (no proxy config reload → live WebSockets are never force-closed). **Reversed the v0.5 executor-tier-separation decision**: the monolith (UI + orchestrator + Hangfire) now ships as **one versioned unit** (D-bg-1) — the draining old release keeps its own orchestrator alive until its in-flight deployments finish, so no tier decoupling. Added **D7**; control-plane `release` registry + `current_default_release` pointer; YARP added to the stack, §16, and §19. |

---

## 1. Purpose & scope

KrakenDeploy today is a single-instance product: one deployment, one database, one set of users, multiple **Spaces** inside it (Spaces are the *within-customer* boundary, carried in the URL as `/s/{slug}` — the space-in-URL routing work). This document designs the layer **above** Spaces needed to run KrakenDeploy as a **SaaS** for many independent customers ("business accounts") from a shared codebase, with a self-service portal that provisions a new account end-to-end.

A **business account** is a fully isolated instance of KrakenDeploy — its own users, its own Spaces, its own data — that *mimics a standalone install*. Accounts never share users or data. The boundary between two accounts is a **cross-customer** boundary: a leak is a reportable GDPR incident, not a cosmetic glitch. This is a strictly higher bar than the Space boundary.

Out of scope for v0.1: billing/metering, the legacy on-prem single-install topology (unchanged — see [`on-prem-guide.md`](on-prem-guide.md)), and cross-account analytics (explicitly not a goal).

## 2. Goals / non-goals

**Goals**
- One stateless app tier serving all accounts (multi-version at runtime via the slot scheme, §13); the active account is resolved per request from the **subdomain**.
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

Locked for this draft (open ones in §18):

- **D1 — Account is the top tenant boundary; Spaces nest inside it.** `Account → (Users, Spaces, …) → Space-scoped resources`.
- **D2 — Subdomain identifies the account** (`acme.krakendeploy.com`). Spaces stay in the path beneath it (`acme.krakendeploy.com/s/{slug}`). Rationale: the browser scopes cookies per host (RFC 6265), so a host-only / `__Host-` session cookie is isolated per account *for free* — no shared global "active account" value to flip across tabs (the failure mode that killed the global Space cookie), and the cookie/TLS/CSP boundary lines up with the tenant boundary.
- **D3 — Database-per-account by default**, with the catalog supporting the full density spectrum. The connection string is the isolation boundary; this eliminates the row-level cross-customer leak class entirely for the default tier. Row-level sharing (multiple accounts per DB + `account_id` discriminator + RLS) is an *optional* density tier, not the baseline.
- **D4 — Users isolated per account** (one user ↔ one account). No shared identities, no account switcher, no cross-account membership table.
- **D5 — Build catalog + resolution in-house.** No Finbuckle/ABP. The resolution pattern is the same shape as the existing `ISpaceContext` one level down; the hard parts (catalog, connection routing, provisioning, migration fan-out) are custom regardless. See §16.
- **D6 — Self-service provisioning via a control-plane portal**, executed as an idempotent, compensating async workflow (§10), behind abuse guardrails (§15).
- **D7 — Multi-node SaaS upgrades use the blue-green slot scheme; the monolith ships as one versioned unit.** Three fixed slots per app node, releases rotate round-robin, version-pinned by an opaque `release_id` cookie, with a per-node YARP router and a version-agnostic HA Caddy edge (§13; full design in [`blue-green-slot-deployment.md`]). This **supersedes** the v0.5 "separate the executor tier" direction: the draining old release keeps its *own* orchestrator running until its in-flight deployments finish, so UI + orchestrator + Hangfire co-deploy as a single unit. ~~Single-node installs use neither slots nor YARP (stop → migrate → start) (**D-bg-5**).~~ **BG1 note (2026-08-27): D-bg-5 is superseded** — the slot scheme is also available ON-PREM (`Deployment:Topology=OnPremBlueGreen`, registry in KrakenDb's `platform` schema via `PlatformReleaseDbContext`; single box is the supported minimum), and `MultiAccount:Enabled` was replaced by `Deployment:Topology` (Saas = this document's mode). `Topology=OnPrem` (the default) keeps stop → migrate → start. See `blue-green-slot-deployment.md` §12.

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

*The diagram is the logical request-resolution view. Physically, the edge is an **HA, ≥2-node Caddy** front (D-bg-6) — optionally behind a DO L4 LB — and each app node internally runs a **per-node YARP router + three release slots** (§13).*

The **app tier** is one deployment (scaled for HA exactly as [`ha-pair.md`](ha-pair.md) describes), stateless, sharing DataProtection keys. It does not "know" accounts at build time — it resolves the account from the `Host` header on every request. Its internal multi-*version* topology — three fixed slots per node with a per-node YARP router pinning each request to a *release*, behind a version-agnostic Caddy edge — is detailed in §13 and the companion [`blue-green-slot-deployment.md`]. A slot is a *version*, not an account or node binding, so the tier stays pooled (any node, any slot, serves any account).

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

**Agent plane** resolves account the **same host-derived way**. Agents connect to their account subdomain (`<sub>.krakendeploy.com`); the `/hubs/agent` WebSocket upgrade and the `/kraken.*` gRPC calls pass through the same `AccountResolutionMiddleware`, so `Request.Host` selects the account before any tenant `DbContext` opens. The agent's bearer `AgentJwt` still carries only `targetId` — the `Host` selects the tenant, and the `targetId` must exist in that tenant's DB (globally-unique `Guid.CreateVersion7()`, so a foreign target fails closed). No account claim in the token, **no cross-tenant `targetId→account` index**. The hub must read the account via `Context.GetHttpContext()` (the `IHttpContextAccessor` is null inside SignalR hub method invocations) and `WithAccount` the connection; unresolved → `Context.Abort()`. Full design + the two prerequisite enrollment/hub bugs: [`design-agent-transport-account-awareness.md`].

## 8. Catalog (control-plane database)

Small, central, always-available, heavily cached. Routing metadata only — **no customer PII** (keeps data residency clean and limits the blast radius of the one shared DB).

```sql
-- Illustrative; final shapes via EF migration in the control-plane project.
CREATE TABLE business_accounts (
    id              uuid PRIMARY KEY,
    subdomain       text NOT NULL UNIQUE,          -- normalized, lower-case, validated
    display_name    text NOT NULL,
    status          text NOT NULL,                 -- Provisioning | Active | Suspended | Deprovisioning | UPGRADING (breaking-change straddle, §13)
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

-- Release registry + default pointer (control-plane scope; see blue-green-slot-deployment.md §4).
-- ~one row per known release; cached per router/node with explicit invalidation on a default flip.
CREATE TABLE release (
    release_id     text PRIMARY KEY,                -- opaque id carried in the __Host-kd_ver cookie
    label          text NOT NULL,                   -- human label / build number
    slot_no        smallint NOT NULL,               -- 1 | 2 | 3
    status         text NOT NULL,                   -- Deploying | Active | Draining | Retired
    deployed_at    timestamptz NOT NULL,
    drained_at     timestamptz,
    drain_deadline timestamptz                       -- max time to keep Draining for idle circuits
);

-- Single pointer for "where new sessions/agents go" (one-row settings table, or a typed catalog key)
current_default_release : release_id
```

Connection secrets live in a secrets store (the platform's existing mechanism — Windows DPAPI/keystore or a vault), and the catalog stores only a *reference*. The catalog is read on every request, so it is cached per app instance with explicit invalidation on account/shard change.

**Entitlements / quotas** are a control-plane concept tied to the account's `tier` — Octopus surfaces these on the subscription (Machines 10 / Projects 10 / Tenants 10 / Users 10 / Spaces 1 / Task cap 5). Store the caps on the account (or a `tiers` table the account references: machines, projects, tenants, users, Spaces, task cap, storage); the **instance reads its entitlements from the catalog (cached) and enforces the caps in-app**. This is also the natural hook for metering/billing later (out of scope for v0.x). The control plane additionally has its **own users + roles** (Subscription Owner / operator), distinct from per-instance app users — keep these in the control-plane DB, not a tenant DB.

## 9. Identity, users & OIDC

Because users are isolated per account (**D4**) and the default tier is database-per-account, identity is *simpler*, not harder:

- Each account database has its own `AspNetUsers` / `AspNetUserLogins`. The same person's external identity (e.g., one Google `sub`) maps to a **separate user row in each account's database** — no uniqueness collision, no composite primary keys, no custom Identity store. **The `businessAccountID`-in-PK idea is unnecessary in this tier** — separate databases give separate user tables for free. (Composite uniqueness `(account_id, normalized_email)` / `(account_id, provider, providerKey)` is needed *only* in the row-level density tier.)
- **Per-account IdP config:** the existing `IdentityProviders` table lives inside each tenant DB — every account brings its own Google/Entra/ADFS, or uses a platform default. See [`docs/oidc-templates`](oidc-templates).
- **The OIDC redirect-URI question — resolved IN-PROCESS (IMPLEMENTED 2026-06-30).** OIDC redirect URIs are per-host. Per-account SSO is done **in-process**, not via the central broker sketched below: a request-time `IAuthenticationSchemeProvider` synthesizes per-tenant schemes (`oidc_{accountId:N}_{providerId:N}`) from the resolved account's own DB, with tenant-keyed `OpenIdConnectOptions` loaded lazily + evicted on edit. Challenge, callback, and cookies all stay on the **tenant's own subdomain** — each account brings its own IdP and registers a redirect URI on its own subdomain (`https://acme.krakendeploy.com/signin-oidc_…`), so there is no wildcard-redirect problem and no second host or cross-domain token handoff. It reuses the single-instance OIDC sign-in handler verbatim. See [`docs/saas-per-account-sso.md`](saas-per-account-sso.md) for the design, security model, and the rationale for choosing this over the broker.

  The **central auth-callback broker** below was the original sketch and was NOT chosen (it breaks the host-only cookie — the broker can't set the tenant host's cookie — forcing a bespoke signed cross-domain handoff, and its one benefit, a single stable redirect URI, is weak when each tenant configures its own IdP). It remains the right pattern only if a first-party shared IdP is introduced later; the in-process design does not preclude it. Original sketch, deferred:

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

One identity-plane account can be a control-plane Subscription Owner of several accounts *and* a separate app user inside each instance — one global identity, isolated per-plane user records. That is exactly your "one consultant, two accounts, two separate users" outcome. A LAUS-internal admin console (manage all tenants) is needed regardless; a *customer-facing* multi-account Control Center is optional (see §18).

**For KrakenDeploy — two different OIDC concepts, don't conflate:**

- **Per-account customer SSO (priority for public-sector):** each institution federates its OWN Entra/ADFS to ITS instance, so employees sign in with their corporate identity and that PII stays at the institution. This is the per-tenant `IdentityProviders` model described above, and it matters most for the target audience.
- **Central platform IdP (`id.krakendeploy.com`) — optional, defer.** It buys cross-plane SSO + a social-login hub + a single redirect target, but it concentrates platform-wide identity PII into one high-value store. Not needed for v1: the Control Center can authenticate against the control-plane DB, and the per-subdomain redirect-URI problem is already solved by the central **auth-callback domain** described above without a full IdP. If built later, self-host it in-region (Duende IdentityServer / Keycloak) — do not put public-sector identities at a third party.

## 10. Self-service portal & provisioning

A control-plane **portal** (`signup.krakendeploy.com` / `app.krakendeploy.com`, a non-account host) lets a prospective customer create an account. Provisioning is a **single orchestrated, idempotent, compensating workflow** run as a Hangfire job — never an inline request, because it touches DNS + a new database + migrations + the catalog and may take seconds to minutes.

**Signup inputs (captured up front).** Organization/display name; desired **subdomain** (with a live availability check, à la Octopus's "`x.octopus.app` is available"); **region** (drives shard placement = data residency, §14); and **terms acceptance** (consent record — version + timestamp, §15). The signing-up owner becomes the account's first admin.

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

### Abuse & cost guardrails (mandatory — see §15)

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

- **Agent control-plane traffic rides the same wildcard.** Agents connect to `<sub>.krakendeploy.com` for both the SignalR hub (`/hubs/agent`) and gRPC (`/kraken.*`), so they need no agent-specific DNS/TLS. The one requirement is that the edge **preserve the `Host` header** to the app tier on those paths (account resolution reads `Request.Host`, not `X-Forwarded-Host`). Verified for the reference Caddy config: the `reverse_proxy` blocks set only `X-Forwarded-Proto`/`X-Forwarded-For` and **no `header_up Host`**, so Caddy forwards the client `Host` by default for the WebSocket and `h2c` gRPC handlers ([`deploy/caddy/Caddyfile`](../deploy/caddy/Caddyfile)).

## 12. Fleet migrations

Database-per-account moves correctness risk into **fleet operations**: every schema change must apply to every tenant DB. Required: an **idempotent migration orchestrator** that enumerates accounts from the catalog, applies pending EF migrations to each distinct connection, tracks per-DB migration state, and **fails loudly on drift** (a DB that missed a migration must surface, not silently diverge). Provisioning a *new* account applies the full migration set as part of `SchemaMigrated`. Align the orchestrator with the existing `dotnet-ef` setup (startup-project = the Data project, `--framework net10.0`). This is the single largest operational cost of the chosen model and must be built early (Phase 2).

**Per-account maintenance windows** (an Octopus-style setting stored on each account) let the orchestrator **stagger** rollouts — apply each account's pending migrations inside *its* nominated window rather than all at once. Batches by window; surfaces any account that fell behind.

Note: per-account staggering applies to **DB schema only**. The shared app binary flips *every* tenant at once (§2, §5), so during a slot overlap two releases run against tenant DBs at different schema states — fleet migrations must therefore follow the **expand/contract** discipline in §13 to stay backward-compatible.

## 13. Self-upgrade: blue-green slot deployment & drain (DigitalOcean)

> **IMPLEMENTED 2026-07-02** — release registry (catalog `app_releases` + `current_default_release`), per-node `KrakenDeploy.Router` (YARP direct forwarding), slot telemetry, `releases` CLI orchestration, drain-watcher, and the agent `X-KD-Release` pin echo; smoke-verified end-to-end (`scripts/smoke-bluegreen.sh`, CI smoke job). See `blue-green-slot-deployment.md` → Implementation notes for deviations and learned requirements (shared per-node DataPath!).

KrakenDeploy deploys software for a living, so it must upgrade *itself* without dropping a single live circuit or live deployment. The mechanism is the **blue-green slot scheme** in the companion **[`blue-green-slot-deployment.md`]** (read it for the full model, the cookie, drain/retire rules, and the topology diagram); this section maps that scheme onto the **DigitalOcean-managed SaaS path**. For self-managed multi-node and plain-VM cloud installs — where you operate the load balancer, Postgres HA, and the rollout yourself — see the companion [`self-upgrade-ha.md`]; the shared engineering spine (two-class state model, idempotent resumable steps, expand/contract migrations) is identical, only the platform mechanics differ.

> **Supersedes the v0.5 §13 — for *version* upgrades.** v0.5 used an in-place, node-by-node rolling drain with a *separate executor tier* as the upgrade mechanism. The slot scheme replaces that: an app-version release is deployed to a spare slot and cut over by a default flip (not in-place node replacement), and the orchestrator co-deploys with the UI as one versioned unit (see below). **Node-by-node drain is not gone** — it is repurposed for *node/host maintenance* (OS patch, kernel, reboot, host-level breaking change): drain a whole node (all three of its slots) via Caddy's health-check fallback, independent of any L4 LB (runbook in [`self-upgrade-ha.md`] §6). The two are orthogonal: a slot flip drains one *release* across every node; a node drain drains every *slot* on one node. The v0.5 revision-history row is kept as the historical record.

### Two classes of state — not equally precious

Any upgrade puts two very different things at risk, and conflating them is the trap:

| State | On a slot drain / node loss | Criticality |
|---|---|---|
| **Blazor circuit** (a user's UI session — the SignalR connection + server-side render tree) | Stays on its (now Draining) release until idle; past `drain_deadline` it re-pins to the default release and reconnects | **Recoverable.** Cosmetic. `.NET 10` circuit state persistence makes it near-seamless. |
| **In-flight deployment** (a running task pushing a release to a customer's targets) | Half-applied changes on a production machine at a *government customer* | **Irreplaceable, side-effecting.** Must never be abandoned or double-run. |

Everything below follows from this ordering: **protect running deployments absolutely; treat circuits as best-effort.** The slot scheme protects *both* — old work finishes on the release that started it — but the ordering is what justifies never force-retiring a Draining slot that still holds an in-flight deployment. Most Blazor-Server rolling-deploy guidance optimizes only circuit preservation; for us that is the *secondary* goal.

### The monolith ships as one versioned unit (not a separate executor tier)

A **slot** is one full monolith instance — Blazor UI **+** deployment orchestrator **+** Hangfire — running one release, versioned and deployed as a single unit (**D-bg-1**). In-flight deployments survive an upgrade two ways: a gracefully **Draining** slot keeps its **whole** monolith — orchestrator included — running until its circuits and in-flight deployments finish, then retires (its orchestrator is never killed mid-deployment); and on a *hard* slot/node loss the durable step ledger + heartbeat lease let a same-release executor reclaim the orphaned task (see *Durable, resumable deployment execution* below). The drain covers the graceful path; the ledger covers the crash path.

This **reverses the v0.5 direction.** Drafts up to v0.5 separated the executor tier from the web tier so a UI release wouldn't kill running deployments. The slot scheme makes that separation unnecessary — the old release's orchestrator survives its own deployments *by draining* — so UI and orchestrator co-deploy as one unit, removing a tier and the UI-release-coupled-to-deployment-duration problem the separation was meant to solve. Tier decoupling is no longer a goal for the SaaS tier.

### The slot model on DigitalOcean

Three fixed slots (`slot1/2/3`) per app node are permanent infrastructure; releases rotate through them round-robin and the routing config stays static ([`blue-green-slot-deployment.md`] §2). DigitalOcean supplies the managed building blocks under that topology:

- **Front tier — Caddy, HA, no DB.** ≥2 Caddy nodes do TLS termination, WebSocket pass-through, cert renewal, and **node-level sticky sessions** (`lb_policy cookie kd_node`). Caddy is **version-agnostic** — it knows nothing about releases or slots and needs no catalog/DB access (**D-bg-6/7**); since it is internet-facing, keeping DB credentials off it is a real attack-surface reduction. Front the pair with a **DO Load Balancer in TCP/passthrough** mode (Caddy keeps terminating TLS, exactly as on-prem); the DO LB gets its **own** stable public IP (kept by never tearing it down — for DOKS, never delete the `LoadBalancer` Service), because a **DO Reserved IP attaches only to Droplets, not to a Load Balancer**. Node affinity is done entirely by Caddy; the DO LB is intentionally session-agnostic — **DO LB sticky sessions don't work with SSL passthrough**, so they are not used. A single Caddy node is a fleet-wide SPOF — run two or more (a Droplet pair with keepalived/VRRP behind a **DO Reserved IP**, or the DO LB as the L4 front).
- **App tier — each node runs YARP + all three slots, with DB access.** A per-node **YARP** router reads `current_default_release` + the `release_id → slot` map from the catalog (cached) and routes each request to the matching **local** slot; cookieless / `Retired` requests go to the local default slot and get `Set-Cookie: __Host-kd_ver=<current_default_release>`. Because YARP only chooses slots, it sits **beside the slots** on the node, so the slot decision is localhost. App nodes are DOKS pods or a Droplet pool (matching the [`ha-pair.md`] VM model); only they hold DB credentials.
- **Version pin — `__Host-kd_ver`** carries an opaque `release_id` (not a raw slot number, so it stays unambiguous while a slot's deploy is mid-rollout across nodes); agents use an `X-KD-Release` header on their persistent connection. Every live release is additive-compatible, so the pin is schema-safe convenience, **not** a security boundary, and is unsigned (**D-bg-2**).
- **Why YARP owns version routing — not Caddy, not proxy config (D-bg-3).** A cookieless request must route to the *current default* release, which means reading the catalog; Caddy can't do that without a config reload per deploy, and a Caddy reload force-closes every live WebSocket fleet-wide. YARP's `InMemoryConfigProvider` is swapped at runtime on the default flip, and reloads apply **atomically to new requests only** — live SignalR circuits and WebSockets are never dropped by a deploy.
- **Two-level affinity (forgiving).** Caddy pins a circuit to a **node** (`kd_node`); YARP pins it to a **release** (`kd_ver`). Because `kd_ver` pins a *release, not a node* and every node runs every slot, a missed node affinity at worst lands a *fresh* circuit on the *same release* on another node (additive-safe; `[PersistentState]` restores annotated state) — never a wrong-version circuit ([`blue-green-slot-deployment.md`] §7).
- **Database:** DO Managed PostgreSQL for the shards **and** the catalog — which now also holds the small control-plane **`release` registry** + the **`current_default_release`** pointer ([`blue-green-slot-deployment.md`] §4). Standby/failover, backups, and PITR are the platform's job (the key reason DO is the launch target); §12 applies migrations against the managed clusters.
- **Shared state across nodes (mandatory for >1 node):** the Data Protection key ring *and* the artifact/package store live on **DO Spaces** (S3-compatible) — the `IArtifactStore` seam the rest of the design implies but doesn't yet name. Without a shared key ring, antiforgery tokens and the `__Host-` session cookie issued by one node fail to decrypt on another.
- **Images:** DO Container Registry. Force the Blazor/SignalR transport to WebSockets (disable long-polling) so circuits ride a long idle timeout rather than the short default — in TCP/passthrough mode the relevant figure is the LB's **connection** idle timeout (the 60-second default applies to HTTP-mode forwarding rules, not raw TCP).

### Durable, resumable deployment execution (the non-negotiable part)

A deployment is a sequence of **steps** with state persisted transactionally in Postgres (`Pending → Running → Succeeded | Failed`), in KrakenDeploy's **own** schema — not delegated to Hangfire's job store (Hangfire triggers/schedules; the durable step ledger is ours, given `Hangfire.PostgreSql` is community-maintained). Requirements:

- **Idempotent steps** — re-running a step that partially ran does not double-apply or corrupt the target.
- **Claim with a lease** — an executor claims a task via `SELECT … FOR UPDATE SKIP LOCKED` and holds a heartbeat lease; if its slot dies, the lease expires and another executor on the *same release* (any node) **resumes from the last completed step** — never two executors on one release.
- **Resume, don't restart** — on restart the task continues from its persisted step, never from the top.

This is what makes a mid-upgrade slot retirement safe, two ways. The orchestrator runs **inside** the slot (D-bg-1), and a Draining slot is allowed to *finish* its deployments rather than being killed — so in-flight work completes on the release that started it. And because the step ledger lives in Postgres, if a whole node is lost a surviving slot on the same release reclaims the expired lease. (No separate executor tier — see above.)

### Expand/contract migrations — extends §12

§12 covers migration *fan-out* and *staggering* (per-account windows). It does **not** cover *coexistence*: the SaaS app tier is **one shared binary serving all accounts** (§2, §5), so an app upgrade flips *every* tenant at once — you cannot stagger the app per tenant the way you stagger DB migrations. While a slot overlap is in progress, **two releases run simultaneously**, against tenant DBs at *possibly different* migration states. Only backward-compatible migrations survive:

1. **Expand** — additive only (new nullable columns/tables; no renames/drops, no NOT-NULL-without-default). Apply across the fleet (staggered per §12 windows) *before* the new release is deployed to its slot. Both releases must run against both schema shapes.
2. **Deploy + flip** — deploy the new release to the idle (`Retired`) slot, health-gate it, then flip `current_default_release`; the new release tolerates pre- and post-expand schemas, and the previous release drains.
3. **Contract** — in a *later* release, once the pre-contract release is `Retired` and every tenant DB is on the new version, drop old columns and tighten constraints.

A breaking migration shipped in lockstep with a release will fault mid-overlap. Under the shared-app-tier + slot model this discipline is mandatory, not optional.

### Slot deploy runbook

```
0. Target slot = the Retired (idle, fully drained) slot. If none, wait or add a slot.
1. EXPAND migrations (additive) across catalog + tenant shards (staggered per §12). Verify no drift.
2. Build image → DO Container Registry. Deploy the new release → target slot on every app node.
   Mark the release Deploying.
3. Health-gate the new slot (synthetic login + a sample job). Do NOT flip until green.
4. Flip current_default_release → new release (Active); mark the previous default Draining.
   Invalidate the catalog cache on every per-node YARP router (in-memory config swap).
5. New sessions / agents get the new release (cookie / X-KD-Release). Existing ones stay pinned
   to their now-Draining release until their work finishes.
6. Draining release takes no new work; its circuits + in-flight deployments finish naturally.
   When active circuits == 0 AND in-flight deployments == 0 (or past drain_deadline for idle
   circuits), mark it Retired → next target.
7. CONTRACT migrations in a LATER release, once the pre-contract release is Retired.
```

On **DOKS** the three slots can be separate `Deployment`s (one per slot) behind the per-node YARP, or three container instances per node; a `PodDisruptionBudget` keeps each slot above its minimum, and a Draining slot's pods are **never** force-killed while they hold an in-flight deployment lease. On **Droplets** you drive the flip + drain yourself. Full drain/retire rules and the topology diagram: [`blue-green-slot-deployment.md`] §6–§9.

### Breaking changes ride a straddle release

The slot scheme carries **additive** releases with zero downtime; a genuinely **breaking** schema change is *not* solved by the slots alone. Use the per-account `UPGRADING` status (§8) + queue-quiesce + a **straddle release** (a build tolerating both schemas): the straddle release rides the slots like any other — deploy to a slot, flip the default — then run the breaking DDLs under `UPGRADING` (batched shard by shard), then deploy the final new-schema-only release into the next slot. The slots carry the releases; the schema safety comes from straddle + quiesce ([`blue-green-slot-deployment.md`] §10).

### Bootstrap caveat

KrakenDeploy upgrading the control plane is the snake eating its tail: the process running the rollout cannot drain itself to zero. Drive control-plane upgrades from a surviving node (rolling, never all-at-once) or an external trigger. The control-plane portal/provisioner is a smaller, separate surface and need not be slotted — roll it node-by-node from a surviving node. The tenant data plane has no such constraint — it is upgraded *by* the control plane via the slot scheme above.

## 14. Backup, restore, erasure, residency

Database-per-account makes per-customer data lifecycle natural:
- **Backup/restore** per account (per-database).
- **Right-to-erasure** = drop the account's database (clean, complete).
- **Data residency / customer-managed keys** = place the account's database on a shard in the required region / with the required key — a catalog/shard decision, no app change.

## 15. Security considerations

- **Cross-customer boundary.** The account boundary is stronger than the Space boundary; treat resolution as security-critical and fail closed (unknown/disabled subdomain → no DB, no default).
- **Never trust the host alone for data.** The subdomain selects the candidate; the resolved connection string is the boundary. In the row-level tier additionally validate `account_id` at every layer and back it with RLS.
- **Version-pin cookie is not a security boundary.** `__Host-kd_ver` is an *unsigned*, opaque `release_id` (D-bg-2) used only to pin a request to an additive-compatible release. A tampered/stale value at worst routes to another *valid* release (never cross-account, never a wrong-schema fault); an unknown/`Retired` value falls back to the default. It carries no authorization meaning — the account boundary remains the resolved connection string. (Routing safety, not a tenant boundary; that is why it needs no signing.)
- **Secrets.** Connection strings are secret-store references in the catalog; the app role per tenant DB is least-privilege (and non-owner / non-`BYPASSRLS` if RLS is in play).
- **Provisioning abuse.** See §10 guardrails — for public-sector go-live, do **not** ship fully-open signup that auto-provisions infrastructure.
- **Per-account IP allow-list (optional, public-sector fit).** An account may restrict its app to specific IP ranges (the institution's networks). Always keep a **platform / trusted-services carve-out** so migrations, health checks, and support are never locked out — Octopus does exactly this ("Trusted Octopus Services always allowed"). Off by default.
- **Consent record.** Capture terms / privacy / acceptable-use acceptance at signup (version + timestamp) as a compliance artifact.
- **Isolation tests (must-have).** Wrong-subdomain → no access; a context-less Hangfire job is denied (not defaulted); provisioning compensation leaves no orphaned DB/DNS/secret; (row-level tier only) RLS denies cross-account rows even with the app filter removed, and a pooled-connection context-bleed test.
- **GDPR.** Per-account erasure + residency satisfied by the silo path; the catalog deliberately holds no PII.

## 16. Affected components / blast radius (existing codebase)

- **New: control plane** — catalog DB + EF model, `IAccountResolver` / `IAccountContext`, account-resolution middleware, `IAccountProvisioner` + step providers, the signup/management portal, the central OIDC callback host, the migration orchestrator.
- **New: version routing & slots** (§13) — a per-node **YARP** router (`release_id → slot`, in-memory config swapped on the default flip), the control-plane **`release` registry** + **`current_default_release`** pointer in the catalog, `__Host-kd_ver` cookie issuance + the `X-KD-Release` agent header, and the three-slot app-node layout behind an HA, version-agnostic Caddy edge. Detail: [`blue-green-slot-deployment.md`].
- **`KrakenDeploy.Server.Data`** — `DbContextFactory` becomes account-aware (connection from `IAccountContext`); migration tooling extended for fleet apply; (row-level tier only) named query filter + RLS.
- **`KrakenDeploy.Server`** — host-based pipeline split (account host vs control-plane host); host-only `__Host-` session cookie; Hangfire jobs carry account context; the existing Space resolution (`SpaceScopedComponentBase`, `SpaceUrlRedirectMiddleware`, `ISpaceContext`) is **unchanged** and runs inside the account.
- **`KrakenDeploy.Server.Transport`** — `AgentHub` + the three `Grpc*Service` classes become account-aware via host-derived resolution + `WithAccount` (read account from `Context.GetHttpContext()`, fail closed on unresolved); the in-process dispatch channels already carry the account (`TenantWorkItem`). Agents are enrolled against their account subdomain (no new catalog table). The agent connection registry is **in-memory in all modes** (the former `PostgresAgentConnectionRegistry` only wrote a `agent_connections` table nothing read — removed as dead weight; connection state is self-healing on reconnect, so it needs no persistence). Dispatch asserts a target's live connection belongs to the dispatching account (Phase 5). Detail: [`design-agent-transport-account-awareness.md`].
- **Hangfire job store** lives in the **catalog / control-plane DB** in multi-account (the schedule is control-plane fan-out via `PerAccountRecurringJobRunner`), single-instance keeps it in `KrakenDb`. Job bodies bind to the correct tenant DB via `WithAccount`. Net: under DB-per-account the shared base `KrakenDb` holds **no** tenant-specific or platform state — it is the single-instance DB only.
- **File store** (`LocalPackageStore` / `LocalArtifactStore`) is **per-account** in multi-account: scoped stores namespace their tree by the active account id — `{DataPath}/accounts/{accountId}/{packages,artifacts}` — so no two tenants share storage. Platform-global material (Data Protection key ring, license) stays at the `DataPath` root. Single-instance keeps the flat shared tree.
- **Backups + restore** are **per-account** in multi-account: `BackupEngine` dumps the resolved tenant DB (not the base DB), bundles into a subdomain-namespaced directory, includes only that account's file slice, and stamps the owning account into `manifest.json`; each account owns a `kraken.backup:{accountId}` recurring job reconciled from its own `BackupSettings`. The CLI `restore --from <bundle> --account <subdomain>` resolves the account's tenant DB from the catalog, restores into it + the account's file slice, and **refuses** a bundle whose manifest account ≠ the target (no cross-tenant overwrite). Single-instance unchanged. (A conversion-time file relocation is a tracked follow-up.)
- **Auth** — OIDC callback centralized to `auth.krakendeploy.com`; per-account IdP config already exists in-DB.

The recently completed **Space-in-URL** work is preserved as-is; the account layer sits above it.

## 17. Phased delivery

1. **Catalog + resolution** — catalog DB, host→account middleware (cached, fail-closed), account-aware `DbContextFactory`. Spaces/users unchanged inside.
2. **Provisioning + migration orchestrator** — the saga (§10) and the fleet migrator (§12). Load-bearing; build early.
3. **Subdomain + host-only session** — wildcard DNS + wildcard TLS cert (DNS-01); `__Host-` session cookie; verify reverse-proxy `Host`/`X-Forwarded-Host`. (On-demand TLS reserved for custom domains.)
4. **Blue-green slot tier** (multi-node SaaS only; shares the Caddy edge + cookie work with Phase 3) — three-slot app-node layout; per-node YARP router + in-memory config swap on the default flip; `release` registry + `current_default_release` in the catalog; `__Host-kd_ver` cookie + `X-KD-Release` header; version-agnostic Caddy HA edge (§13, [`blue-green-slot-deployment.md`]). Single-node installs skip this entirely (stop → migrate → start, D7).
5. **Self-service portal** — signup UX + status polling + abuse guardrails (start in admin-approved mode).
6. **OIDC via central auth domain** — one registered redirect URI; per-account IdP in-DB.
7. **Isolation + provisioning tests** (§15), including compensation/rollback.
8. **Dedicated/silo promotion + de-provisioning** — catalog-driven move to dedicated DB/server; drop-DB erasure; custom-domain support (later).

## 18. Open questions

- **Signup gating** — fully open vs admin-approved vs invite-only at go-live? (Recommended: not open for public-sector.)
- **Shard placement policy** — how is the target shard chosen (capacity, region, tier)? Manual vs automatic?
- **Subdomain namespace** — globally unique, first-come; reservation/rename policy; reserved list owner.
- **DNS provider** for the public `krakendeploy.com` zone (drives `IDnsProvisioner`), and whether wildcard is acceptable to the zone owner.
- **Control-plane home** — separate project (`KrakenDeploy.ControlPlane`) vs a host-keyed module inside `KrakenDeploy.Server`.
- **Migration rollout safety** — online vs maintenance-window fleet migrations; per-DB failure handling/alerting.
- **Slot count** — three slots (one drain overlap) is the floor; is it enough for KrakenDeploy's deploy cadence vs. the longest in-flight deployment lifetime, or should the SaaS tier provision more? Tuning parameter — [`blue-green-slot-deployment.md`] D-bg-4.
- **Customer-facing Control Center?** — give owners an Octopus-style multi-account console (manage several accounts, billing) or keep each account a standalone signup with no cross-account owner view? (Adds owner PII to the control plane.)
- **Tenant app domain** — subdomains of `krakendeploy.com` (with host-only cookies) vs a *dedicated* app domain (e.g. `*.krakendeploy.app`) for stricter origin isolation and to avoid reserving control-plane names in the tenant namespace.
- **Warm pool** — maintain pre-provisioned, pre-migrated tenant DBs for near-instant signup, or cold-provision per signup?
- **Subdomain rename** — allow changing an account's subdomain later (Octopus "Change URL"), with old-link/redirect-URI handling — or treat it as immutable?
- **Central platform IdP** — build `id.krakendeploy.com` (cross-plane SSO + social logins + single redirect target) or keep auth per-plane (control-plane DB) + per-account customer SSO + the central auth-callback domain? If built: self-hosted in-region (Duende IdentityServer / Keycloak) vs a cloud IdP (residency/processor concern for public-sector). Recommendation: defer; prioritize per-account customer SSO.

## 19. References

- KrakenDeploy: [`architecture.md`](architecture.md), [`ha-pair.md`](ha-pair.md), [`on-prem-guide.md`](on-prem-guide.md), [`blue-green-slot-deployment.md`](blue-green-slot-deployment.md), [`self-upgrade-ha.md`](self-upgrade-ha.md), [`deploy/caddy`](../deploy/caddy), [`docs/oidc-templates`](oidc-templates).
- EF Core multitenancy — https://learn.microsoft.com/ef/core/miscellaneous/multitenancy
- EF Core query filters (named filters, EF 10) — https://learn.microsoft.com/ef/core/querying/filters
- Azure Architecture Center, multitenancy tenancy models — https://learn.microsoft.com/azure/architecture/guide/multitenant/considerations/tenancy-models
- Azure Architecture Center, tenant domain names — https://learn.microsoft.com/azure/architecture/guide/multitenant/considerations/domain-names
- Azure Architecture Center, multitenant identity — https://learn.microsoft.com/azure/architecture/guide/multitenant/approaches/identity
- PostgreSQL Row-Level Security — https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- RFC 6265 (cookie host scoping) — https://datatracker.ietf.org/doc/html/rfc6265#section-4.1.2.3 ; MDN Set-Cookie — https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Set-Cookie
- Caddy on-demand TLS — https://caddyserver.com/docs/automatic-https#on-demand-tls
- Caddy `reverse_proxy` load balancing (`lb_policy cookie`, health checks) — https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
- YARP (reverse proxy) — overview / getting started — https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/getting-started
- YARP extensibility — configuration providers (`IProxyConfigProvider` / `InMemoryConfigProvider`; runtime `Update`, reloads applied atomically to new requests only) — https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/config-providers
- YARP session affinity — https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/session-affinity
- YARP proxying WebSockets and SPDY — https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/websockets
- DO Load Balancers — features (WebSocket, sticky sessions, SSL termination) — https://docs.digitalocean.com/products/networking/load-balancers/details/features/ ; limits (sticky sessions vs SSL passthrough) — https://docs.digitalocean.com/products/networking/load-balancers/details/limits/
- DOKS load-balancer / sticky-session annotations — https://docs.digitalocean.com/products/kubernetes/how-to/configure-load-balancers/
- DO Managed PostgreSQL (standby, backups, PITR, connection pools) — https://docs.digitalocean.com/products/databases/postgresql/
- ASP.NET Core Data Protection — key-ring storage providers — https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview
- Blazor Server circuit handling & state persistence (.NET 10) — https://learn.microsoft.com/aspnet/core/blazor/fundamentals/signalr
- Finbuckle.MultiTenant (evaluated, not adopted) — https://www.finbuckle.com/MultiTenant

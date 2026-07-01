# KrakenDeploy — Agent-Transport Account Identity (P3-8)

| | |
|---|---|
| **Version** | 0.5 |
| **Date** | 2026-07-01 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude Code |
| **Status** | `Approved` — phases 0–8 implemented and verified: build/unit + in-process transport E2E + live real-agent multi-account Docker smoke (green in CI). Only residual: wrong-account *deployment-dispatch* drop-out is unit-covered, not yet exercised end-to-end with a live deployment. |
| **Technologies** | .NET 10, ASP.NET Core / SignalR, gRPC, EF Core 10, PostgreSQL, Caddy |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Transport`, `KrakenDeploy.Server.Data`, `KrakenDeploy.Server.Core`, `KrakenDeploy.Agent`, `KrakenDeploy.ControlPlane` |

## Purpose

This is the design for **P3-8** of the SaaS multi-account retrofit (`saas-phase3-account-awareness.md`): giving the agent transport an **account identity** so the server can bind the correct tenant DB when an agent connects and when a deployment dispatches to it.

The in-process half of P3-8 (the channel carry, `TenantWorkItem(Guid AccountId, Guid Id)`) is **done and build-verified**. What remains is the agent *link* itself: the connection and the gRPC services carry no account, so a connected agent cannot be attributed to a tenant DB. This is a hard cross-customer (GDPR-reportable) boundary per `saas-multi-account-architecture.md` §7/§15 and must fail closed.

**Decision (v0.2): host-derived account identity** — the agent connects to its account subdomain (`<sub>.<base>`) and the server resolves account from the request `Host`, exactly as the web plane does. This reuses the existing resolution machinery, adds **no new cross-tenant table**, and composes cleanly with the later PoP enrollment redesign. v0.1 recommended a control-plane `agent_registry` table; that is reversed — see [Why not a control-plane registry](#why-not-a-control-plane-registry).

## Problem

Under DB-per-account the database a request resolves to *is* the account; there is no `account_id` discriminator on tenant rows (`saas-multi-account-architecture.md` §6). The web plane resolves account from the request `Host` via `AccountResolutionMiddleware` and pins it on `IAccountContext`, which `KrakenDbContext.OnConfiguring` reads to bind the tenant connection string.

The agent transport has no equivalent today:

- `AgentJwtService.Issue(Guid targetId)` mints an HS256 token whose **only** meaningful claim is `sub = ClaimTypes.NameIdentifier = targetId`. No tenant/account/space claim. Issuer/audience are stamped but **not validated** (`ValidateIssuer=false`, `ValidateAudience=false`). Lifetime 365 days, no refresh, no revocation list.
- Agents connect to one shared `Server:Url`; `IAgentConnectionRegistry` maps `connectionId ↔ targetId` with no account dimension.
- A `targetId` is a globally-unique `Guid.CreateVersion7()` but is only resolvable *inside* its own tenant DB.

So the server cannot tell which account a connected agent belongs to, and `AgentHub`/the three `Grpc*Service` classes open a tenant `DbContext` against whatever account is ambient (today: none).

### Verified gap that compounds the risk

`AgentHub.OnConnectedAsync` registers the connection **before** loading the target, and the not-found branch only logs — it does **not** abort:

```csharp
registry.Add(Context.ConnectionId, targetId.Value);              // line 47 — runs first
var target = await db.DeploymentTargets.IgnoreQueryFilters()
    .FirstOrDefaultAsync(t => t.Id == targetId.Value);           // account-blind today
if (target is not null) { /* mark Online */ }
else { logger.LogWarning(...); }                                 // lines 72-77 — NO Context.Abort()
```

This must be closed regardless of approach: account resolution moves ahead of `registry.Add`, and an unresolved account aborts the connection.

## Decision: host-derived account identity

The agent connects to **its account subdomain** — `https://<sub>.<base>` — instead of a shared host. The server resolves the account from `Request.Host` on every agent request (`/hubs/agent` WebSocket upgrade and `/kraken.*` gRPC), through the **same** `AccountResolutionMiddleware` → `CatalogAccountResolver.ResolveAsync(host)` path the web already uses, and enters `IAccountContext.WithAccount` before any tenant `DbContext` opens.

The agent's target host is a **single stored string** (`AgentIdentity.ServerUrl`, set at registration — `RegistrationHostedService.cs:131`) that drives both the SignalR hub and all three gRPC channels (`GrpcPackageDownloader`/`GrpcArtifactUploader`/`GrpcStepPackageDownloader`, all reading `ctx.Identity.ServerUrl`). So "per-account subdomain" is one value handed to the agent at enroll, when the operator is already in the account's circuit — not new infrastructure.

### Why this is solid (not "uniqueness by luck")

Account is selected **positively** from the `Host` (`ResolveAsync(host)`, `Active`-only, fail-closed). The `targetId` from the JWT is then loaded *within that account's tenant DB*. Because `targetId` is a `Guid.CreateVersion7()` (122 random bits), a target belonging to another tenant simply does not exist in the host-selected DB → the connection is rejected. This is the same uniqueness guarantee every primary key in the system already relies on, used here as an *existence proof* on top of a positive host selection — equivalent rigor to a catalog lookup, not a probabilistic dodge.

Stolen-JWT blast radius is identical to any alternative: the token is confined to its own tenant either way (its `targetId` only resolves under its own subdomain / in its own DB). The shared HS256 key proves "some target in the fleet"; the `Host` + DB-existence pins *which* tenant.

### How it binds, end to end

| Stage | Mechanism | Reused / new |
| --- | --- | --- |
| Enroll | `TargetRegistrationService.CreateAsync` runs in the operator's resolved tenant circuit; the enroll response / install command hands the agent `https://<sub>.<base>` as `Server:Url`. No catalog write, no `targetId` leaves the tenant DB. | Enroll response carries the subdomain URL |
| Register | `POST /api/agents/register` (`AllowAnonymous`, rate-limited) consumes the one-time token and issues `AgentJwtService.Issue(target.Id)`. **Unchanged.** | Unchanged |
| Connect | The `/hubs/agent` WebSocket upgrade passes through `AccountResolutionMiddleware` (already in the pipeline, before auth), which resolves account from `Request.Host` and stashes it in `HttpContext.Items`. `AgentHub.OnConnectedAsync` reads it from `Context.GetHttpContext()` (**not** `IHttpContextAccessor` — that is null inside hub method invocations), `WithAccount`s the connection, then loads `targetId` in the now-correct tenant DB. Null account or `targetId`-not-found → `Context.Abort()`. | `ResolveAsync` + middleware reused; `WithAccount` wrap + Abort new |
| Hub callbacks | The same connection-scoped account applies (`Heartbeat`/`AppendLog`/`CompleteDeployment`/`ReportStepCompleted`/`ReportAdhocResult`). Cache the `ResolvedAccount` on the connection (`Context.Items`) and re-enter `WithAccount` per callback. The `IgnoreQueryFilters` ownership/entitlement checks become sound because they now run under the right account. | New wrap |
| gRPC | `Grpc*Service` calls run in the request context, so `IHttpContextAccessor.HttpContext` **is** reliable; account resolves through the same middleware + `OnConfiguring` chain. They already use `httpContextAccessor` for the target. | Largely automatic |
| Dispatch | `DeploymentWorker` already resolves `item.AccountId` → `WithAccount` → `registry.GetConnectionId(target.Id)` (the in-process `TenantWorkItem` half is done). Add an assertion that the connected target's resolved account equals the dispatch account; mismatch → offline drop-out, never a cross-account push. | Reused + guard |

`ResolveAsync(host)` is already the right primitive (the web uses it): cached, `Status == Active` only, fail-closed. `IAccountContext.WithAccount` already flows an account across awaits and child DI scopes via `AsyncLocal`.

### Fail-closed semantics

In multi-account mode: an agent request with no resolvable subdomain, an inactive/unknown account, or a `targetId` not present in the host-selected DB → rejected (`Context.Abort()` at the hub; throw before any `DbContext` opens for gRPC). `IAccountContext` itself throws on unresolved reads (no default fallback), so even a path that skips `WithAccount` fails closed rather than hitting an ambient DB. `AccountResolutionMiddleware` currently passes apex/non-navigational requests through (`:52`/`:64`); for agent paths this must **require** a resolved account, never default.

## Edge requirements (verified)

- **Host is preserved by the edge.** The reference Caddyfile's `reverse_proxy` blocks set only `X-Forwarded-Proto` and `X-Forwarded-For` — there is **no `header_up Host`** — so Caddy v2 forwards the client's original `Host` upstream by default. This holds for the `/hubs/*` (WebSocket, `flush_interval -1`) and `/kraken.*` (gRPC, `h2c://`) handlers. Therefore `Request.Host.Host` reaches Kestrel intact and account resolution needs no `X-Forwarded-Host` / `UseForwardedHeaders` / `KnownProxies` configuration. (`deploy/caddy/Caddyfile`, `deploy/onprem/Caddyfile`.)
- **Wildcard site + cert is the one new edge need — shared with the web.** The current site block is single-host (`{$DOMAIN:localhost}`). Multi-account needs a wildcard block (`*.{$DOMAIN}`, plus the apex for the control-plane landing) and a wildcard TLS certificate, which forces ACME **DNS-01** issuance (HTTP-01 cannot issue wildcards → a Caddy DNS-provider module is required). This is required for the web plane regardless; the agent rides the same wildcard, so there is **no agent-specific edge work**.

## The two bugs to fix regardless

Both are real today, verified in code, and independent of which approach is chosen.

1. **Enrollment is broken for non-Default-Space targets.** `TargetRegistrationService.ValidateAndConsumeTokenAsync` (`:146`) runs a plain `FirstOrDefaultAsync` with **no** `.IgnoreQueryFilters()`, while `CreateAsync` three lines up *does*. On the anonymous `/api/agents/register` the ambient context falls back to `WellKnown.DefaultSpaceId` (`HttpSpaceContext`), so a target created in a non-Default Space is hidden by the global filter and its token returns 401 — that target can never enroll. Fix: add `.IgnoreQueryFilters()` + a non-Default-Space test. Ships standalone.
2. **`AgentHub` fails open.** Move account resolution ahead of `registry.Add`, and `Context.Abort()` when it returns null (today the not-found branch only logs).

## Why not a control-plane registry

v0.1 recommended a control-plane `agent_registry(targetId → accountId)` table resolved at connect via `ResolveByIdAsync`. Reversed, for two reasons:

- **It buys nothing host-derived doesn't already give**, while adding a **new global cross-tenant index** (a single table mapping every `targetId` across all customers) — itself an attack surface — plus new store methods, a new resolver, an enroll-time dual-write (tenant DB + catalog, no distributed transaction), and a reconcile job. Host-derived adds **zero new tables** and reuses `ResolveAsync(host)`.
- **Its only genuine advantage was zero agent-side change when converting an existing on-prem fleet** (bare-host JWTs keep working; the server maps `targetId → account`). KrakenDeploy is **pre-production**, so reconfiguring agents to their subdomain URL is a non-issue, and the SaaS product motion is new customers enrolling fresh agents against their subdomain from day one. The advantage is moot.

## Why not the other alternatives

- **JWT `accountId` claim** — rejected. Chicken-and-egg: the anonymous `register` endpoint has no `Host` and resolves to the Default/ambient DB, and `ValidateAndConsumeTokenAsync` reads with no `IgnoreQueryFilters` (bug #1). A non-Default-account target's row is either not found (mint fails) or read from the wrong DB (wrong-account binding); the proposed cutover window admitting claim-less tokens is fail-open. The 365-day, non-revocable, shared-secret token also makes any baked-in binding immutable for a year.
- **Per-account mTLS (cert SAN)** — deferred. Caddy terminates TLS, so the app would authenticate on a forgeable forwarded header unless the internal hop is hardened and the header stripped; it needs a dedicated cert-terminating host (client-cert auth cannot be required on the browser wildcard, Caddy #4696) and full per-deployment PKI the team has no operational basis for. The team's own v0.2 enrollment design (`design-agent-enrollment-cert-auth.md`) already chose **app-layer proof-of-possession** for equivalent key-possession guarantees with a dumb edge — host-derived account selection composes with that (PoP changes the *authenticator*; the `Host` still selects the account, or once PoP lands the tenant-scoped API key can).

## Risks and mitigations

- **No subdomain / wrong subdomain in multi-account** → must reject, never default (the `AccountResolutionMiddleware` apex pass-through must be tightened for agent paths). The connect-time Abort + `IAccountContext` throwing on unresolved reads are the backstops.
- **Reverse-tunnel / future edge change that rewrites `Host`** would silently break resolution. Mitigation: a startup/health assertion that an agent connection carries a resolvable `Host`; do not fall back to `X-Forwarded-Host` unless `UseForwardedHeaders` is configured with `KnownProxies`.
- **Wildcard cert operational dependency** (DNS-01 + provider credentials) — shared with the web; track in the edge runbook.
- **Hub vs gRPC context difference** — `IHttpContextAccessor` is unreliable inside SignalR hub method invocations; the hub must read `Context.GetHttpContext()`. gRPC is fine. This is a correctness trap, not a design flaw.

## Migration

Single-instance mode is a no-op (`DisabledAccountContext`; resolver returns null, callers skip `WithAccount`). Converting to multi-account requires each agent's `Server:Url` to point at its account subdomain — **acceptable because the product is pre-production** (no installed fleet to disrupt). New SaaS customers enroll fresh agents against their subdomain from the start. If a fleet ever needs in-place conversion, the server can push a config update to connected agents (`AgentUpdateService` exists) rather than reverting to a registry.

## Phased plan

Implementation status (2026-07-01): phases **0–8 done and verified** — build/unit + in-process transport E2E + live real-agent multi-account Docker smoke (green in CI). The Phase 5 `AdhocDispatcher` guard, once deferred, is also done. Per-phase detail is tracked in [`saas-phase3-account-awareness.md`](saas-phase3-account-awareness.md) P3-8. Notably the host-derived design made phases 1 and 4 zero-change (the wizard already emits the operator's subdomain via `Nav.BaseUri`; gRPC inherits the account from the middleware), and the agent-side concern reduced to one new filter + one hub edit.

0. **Prerequisite bug, standalone:** `.IgnoreQueryFilters()` on `ValidateAndConsumeTokenAsync` + a non-Default-Space test. **DONE.**
1. **Enroll hands out the subdomain URL.** `TargetRegistrationService.CreateAsync` / the add-target wizard compose `https://<sub>.<base>` (from the resolved account + `MultiAccountOptions.BaseDomain`) into the install command and the `RegisterAgentResponse`. Single-instance: unchanged bare host.
2. **`AgentHub.OnConnectedAsync` host-derived + fail-closed.** Read the account the middleware resolved (`Context.GetHttpContext()` → `HttpContext.Items` / `Request.Host`); null → `Context.Abort()` **before** `registry.Add`; else `WithAccount` for the connection and load `targetId` in the tenant DB; not-found → `Context.Abort()`.
3. **Wrap hub callbacks** in the connection's `WithAccount` (cache `ResolvedAccount` on `Context.Items`).
4. **Verify gRPC** resolves account through the existing middleware + `OnConfiguring`; add the `WithAccount` wrap at method entry if any path resolves a `DbContext` outside it.
5. **Harden dispatch:** assert connected target's resolved account == dispatch `_dispatchAccountId`; mismatch → offline drop-out. **DONE** for `DeploymentWorker` + `RunbookRunWorker` (registry records the connection's account; workers assert it at dispatch) **and `AdhocDispatcher`** — `IAdhocDispatcher.DispatchAsync` now takes `dispatchAccountId` (threaded from `AdhocSessionService`) and blocks a cross-account push; unit-covered by `AdhocDispatcherTests` (accountA/accountB).
6. **Tighten `AccountResolutionMiddleware`** so agent paths require a resolved account in multi-account mode (no apex pass-through). **DONE** — `IsTenantScopedAgentPath` fail-closes (404) `/hubs/agent`, `/api/agents/register`, `/api/agents/update-info`, and the gRPC prefix `/krakendeploy.v1.*` (the proto package is `krakendeploy.v1`, **not** `kraken.*`).
6b. **Connection-registry persistence REMOVED — DONE.** A shared-state audit found the `agent_connections` table was **never read** (the registry only wrote it; all reads are node-local memory), so persistence — per-account *or* shared — was dead weight and a false HA promise (HA correctness rests on sticky-session routing, not the table). `PostgresAgentConnectionRegistry` was **deleted**; `InMemoryAgentConnectionRegistry` is used in all modes. Connection state is self-healing (a dropped agent reconnects and re-registers), so it needs no durability — unlike Hangfire jobs, which must survive restart. The in-memory registry still tracks the per-connection account, so the Phase 5 dispatch guard is unaffected. This **supersedes** the earlier per-account-write change (which was correctly isolating something that should not have been persisted at all). A genuine cross-node registry is deferred until a SignalR backplane exists.
7. **Edge:** wildcard site block (`*.{$DOMAIN}`) + DNS-01 wildcard cert. **DONE** — delivered as a dedicated `deploy/saas/Caddyfile` (single-host `deploy/caddy` + `deploy/onprem` left unchanged); `Host` preserved on `/hubs/*` and `/krakendeploy.v1.*` (gRPC matcher corrected from the stale `/kraken.*`).
8. **Isolation tests (P3-7 overlap): DONE**, in two layers. (a) In-process transport E2E `tests/KrakenDeploy.Server.Data.Tests/MultiAccountAgentTransportE2ETests.cs` — real SignalR pipeline over two tenant DBs: foreign `targetId` on `acme` rejected, bare/unresolvable host rejected, two-account isolation. (b) **Live real-agent Docker smoke** `scripts/smoke-multiaccount.sh` (+ `docker-compose.smoke-multiaccount.yml`) — the shipped agent binary connects to the `acme`/`globex` subdomains, each goes Online in its **own** tenant DB, a cross-account registration-token replay is rejected (HTTP 401), and there is no cross-account leakage; **green in CI** (push-to-main `Smoke Test` job) and locally. Residual: wrong-account *deployment-dispatch* drop-out is unit-covered (`AdhocDispatcherTests` + the `DeploymentWorker`/`RunbookRunWorker` guard), not yet exercised end-to-end with a live deployment.

## Open decisions

- **Subdomain source of truth on the agent** — store the full `https://<sub>.<base>` in `ServerUrl` (simple, current shape), or store base + subdomain separately (cleaner for re-pointing on account rename)? Recommendation: full URL now, it is already a config string.
- **gRPC entitlement under `WithAccount`** — confirm the package/step-package download entitlement checks (the still-open per-target gap, `#6`) are evaluated inside the resolved account; this is where a cross-account package read would surface.

## References

- `src/KrakenDeploy.Server.Transport/AgentHub.cs` — connect-time identity; the not-found fall-through to fix (lines 47, 72-77)
- `src/KrakenDeploy.Server/Accounts/AccountResolutionMiddleware.cs` — host resolution (`:35`), apex pass-through (`:52`/`:64`) to tighten for agent paths
- `src/KrakenDeploy.ControlPlane/Accounts/CatalogAccountResolver.cs` — `ResolveAsync(host)` reused as-is
- `src/KrakenDeploy.Server.Core/Domain/Accounts/HostParser.cs` — `ExtractSubdomain(host, baseDomain)`
- `src/KrakenDeploy.Agent/Services/RegistrationHostedService.cs` (`:131`), `src/KrakenDeploy.Agent/Identity/AgentIdentity.cs` (`:18`) — single `ServerUrl` driving hub + gRPC
- `src/KrakenDeploy.Server.Data/Services/TargetRegistrationService.cs` — `CreateAsync` enroll site; `ValidateAndConsumeTokenAsync` cross-Space bug (`:146`)
- `src/KrakenDeploy.Server.Data/TenantWorkItem.cs`, `src/KrakenDeploy.Server.Transport/DeploymentWorker.cs` — in-process account-carry half (done) and dispatch lookup
- `deploy/caddy/Caddyfile`, `deploy/onprem/Caddyfile` — edge; `Host` preserved by default, wildcard site + DNS-01 needed for SaaS
- `docs/saas-multi-account-architecture.md` (§6/§7/§13/§15/§19), `docs/saas-phase3-account-awareness.md` (P3-8), `docs/design-agent-enrollment-cert-auth.md` (app-layer PoP — composes with host-derived)
- Caddy reverse_proxy Host handling — https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
- ASP.NET Core SignalR `HttpContext` access — https://learn.microsoft.com/aspnet/core/signalr/httpcontext

## History

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-24 | Domagoj Jugović | Initial draft: recommended control-plane `agent_registry`; registry-vs-PoP reconciliation, verified bugs, phased plan. |
| 0.2 | 2026-06-24 | Domagoj Jugović | **Reversed recommendation to host-derived account identity** (subdomain parity): reuses existing resolution, no new cross-tenant table, edge `Host`-preservation verified against the Caddyfile. Registry demoted (its only edge — zero agent reconfiguration — is moot pre-production). |
| 0.4 | 2026-06-26 | Domagoj Jugović | **Shared-state audit + Phase 5 + cleanups.** Phase 5 cross-account dispatch guard done (registry records connection account; workers assert it). A shared-base-`KrakenDb` audit then found: the `agent_connections` table is never read → **deleted `PostgresAgentConnectionRegistry`, persistence removed** (reverses the v0.3 per-account-write); Hangfire job store moved to the catalog/control-plane DB in multi-account; `LicenseUsageCounter` cross-tenant in-memory cache leak fixed (scoped in multi-account). Build 0/0; multi-account boot clean (Hangfire schema lands in catalog, no captive-dep). |
| 0.3 | 2026-06-26 | Domagoj Jugović | **Server-side implemented (phases 0–4)** and build/unit/smoke-verified. Phase 0 enrollment fix + regression test (7/7 green, real Postgres); new `AgentAccountHubFilter` (multi-account-only `IHubFilter`) + `AgentHub.OnConnectedAsync` validate-before-register + fail-closed-on-unknown-target; phases 1 (wizard URL) and 4 (gRPC) were zero-change. Solution builds 0/0; multi-account idle smoke clean (acme/globex `/login`→200, apex→200, unknown→404, 0 "no account" errors). Phases 5–8 + live-agent verification pending. Status → `Review`. |
| 0.5 | 2026-07-01 | Domagoj Jugović | **Phases 5–8 complete + verified; Status → `Approved`.** Phase 5 `AdhocDispatcher` guard done (was deferred); Phase 6 middleware fail-close done (`IsTenantScopedAgentPath` → 404 on hub/enroll/update-info/`/krakendeploy.v1.*`); Phase 7 edge shipped as `deploy/saas/Caddyfile`; Phase 8 isolation tests done — in-process transport E2E **plus a live real-agent multi-account Docker smoke** (`scripts/smoke-multiaccount.sh`) proving host-derived per-account routing + cross-account token-replay 401 + no leakage, **green in CI**. Residual: wrong-account deployment-dispatch drop-out remains unit-covered only. |

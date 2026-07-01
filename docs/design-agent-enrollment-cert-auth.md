# KrakenDeploy — Agent Enrollment & Proof-of-Possession Auth

| | |
|---|---|
| **Version** | 0.2 |
| **Date** | 2026-06-16 |
| **Authors** | Domagoj Jugović (with Claude Code) |
| **Status** | Draft |
| **Technologies** | .NET 10, ASP.NET Core / SignalR, gRPC, Spectre.Console, Caddy, PostgreSQL/Redis, EF Core 10 |
| **Projects** | KrakenDeploy.Agent, KrakenDeploy.Server, KrakenDeploy.Server.Transport, KrakenDeploy.Server.Core, KrakenDeploy.Server.Data |

## 1. Goal

Replace the pre-create-target + one-time registration-token flow with **API-key enrollment** (agent self-creates its target), and secure the **ongoing** server↔agent channel with **application-layer proof-of-possession (PoP)** keyed to an agent-held, non-exportable keypair pinned by SHA-256 thumbprint. The API key is **enroll-only** — never persisted on the target, never used on the live channel.

**Decision (v0.2): Route B (app-layer PoP) is the design.** It works identically on-prem and SaaS, and keeps the edge (Caddy) entirely out of the agent-auth path — no TLS client certs, no `client_auth`, no per-target edge config, no forgeable forwarded headers. (Route A — TLS-layer mTLS — is recorded in §A1 as a rejected alternative.)

> **Why the edge needs zero knowledge of agent keys:** there are no TLS client certs. Caddy terminates its own server TLS and transparently proxies the WebSocket/gRPC, exactly as it already does for `/hubs/*`. All key/thumbprint state lives only in the app DB.

This design was adversarially red-teamed (5 lenses); §7 is the resulting hardening checklist and every Critical/High below is reflected in the relevant section.

## 2. Identity model (decided)

- **API key** — issuable in Settings against **any principal** (user *or* service account; guidance: service account for agents). Shown once, stored **hashed**, **scoped to exactly one account/tenant** (§4-H), enroll-only. Doubles as the API-integration credential. Multiple keys allowed (shared for bulk; per-agent for blast-radius control).
- **Target credential** — at enrollment the agent generates its **own keypair** (non-exportable, OS keystore/TPM). The server pins the **RFC 7638 JWK SHA-256 thumbprint** of the agent public key as that target's identity. No bearer token, no API key on the box.
- **Mutual** — the agent **pins the server's leaf-cert thumbprint** (provisioned out-of-band before enroll, §6) and refuses any other — blocks rogue/MITM server. This agent-side pin is the *real* MITM control; the `srv` claim (§5) is defence-in-depth only.
- **Authorization** — server-side lookup keyed by `recomputed-thumbprint → target → account`. The key proves *who*; the target record decides *what*.

> SHA-256, not SHA-1: `X509Certificate2.Thumbprint` is SHA-1 (deprecated). Pin the RFC 7638 JWK SHA-256 thumbprint, computed **server-side** from the key's raw parameters — never from the agent-supplied JWK JSON.

## 3. Threat model

Defends against: network MITM, rogue server, stolen-proof **replay**, cross-tenant impersonation, credential-on-box theft (only a revocable target credential is exposed, never the API key or a user token).

**Out of scope but explicitly widened (per red-team):** a party with **read/inject access to the Caddy↔app internal hop** — not just a "compromised Caddy". The internal hop (`kraken-server:5080`, h2c/cleartext) MUST therefore be confidentiality+integrity protected (internal TLS / Unix-domain socket / loopback-bound + firewalled to the Caddy host only). Without true end-to-end TLS channel binding (Caddy terminates TLS), channel integrity rests on nonce-freshness + single-use + the agent-side server pin; this is stated as a residual.

## 4. Enrollment flow

```
Settings ── generate API key (hashed, bound to ONE account/tenant, enroll-only) ─┐
install (Spectre.Console verb; runs as service/daemon)                            │
  inputs: server URL + API key  [+ name/roles/tenants silent, or TUI]             │
    ├─ agent generates keypair (non-exportable); computes machine fingerprint     │
    └─ POST /api/agents/enroll  (Authorization: ApiKey)  ◄──────────────────────────┘
         server:
           1. validate key (hash, not revoked, NOT expired) → principal + ACCOUNT SCOPE
           2. NEW target only via the API key. Cap roles/tenants/Space by the key's
              grants; first-enroll lands status=PendingApproval for prod (TOFU pins
              IDENTITY, does NOT auto-grant authorization)
           3. pin recomputed RFC7638 thumbprint of the presented key
              (global-unique; reject duplicate)
           4. return { targetId, serverThumbprintSha256(advisory), agentHostUrl }
    └─ agent persists target identity to agent.json; DISCARDS the API key; connects
```

**4-H Hardening (red-team):**
- **Re-attach ≠ enroll.** Re-attaching to an **existing** target (reinstall / lost `agent.json`) MUST require **proof-of-possession of a currently-trusted key** (sign a server nonce with the existing private key) — never the API key alone. `(machineFingerprint, name)` are non-authenticating **hints**, never identity selectors. A genuinely lost key → explicit **operator-approved re-provision** (revoke old thumbprint + new target), not silent re-attach. *(Closes the critical takeover path.)*
- **Account scoping.** Every enroll/re-attach enforces the key's bound account/tenant server-side; reject any client-supplied account/target selector not matching the key's scope. *(Closes cross-account pinning.)*
- **Global thumbprint uniqueness.** DB unique index on the pinned thumbprint across all targets/accounts; reject enroll/rotate that would duplicate; runtime lookup is **fail-closed** (0 or >1 match → reject), selecting the target by the **recomputed** thumbprint only.
- Audit-log every pin/re-attach/approval with the enroll-key id.

## 5. Ongoing-channel auth — DPoP-style PoP (RFC 9449)

**SignalR (per-connection):** on every (re)connect the server issues a **fresh single-use, short-TTL nonce**. The agent sends a PoP JWT signed by its key with claims `{ cnf, nonce, iat, exp, jti, aud, srv }`. Server validation order is load-bearing:

1. parse header → extract the presented public key (JWK);
2. **recompute** the RFC 7638 SHA-256 thumbprint of that key;
3. **require** it to be a current member of the target's `trusted_thumbprints` — *before* any signature check;
4. verify the signature with that (now-trusted) key, **alg pinned to `ES256`** (no `none`, no HMAC fallback, never the HS256 `AgentJwt` path);
5. require `cnf` == recomputed thumbprint; `nonce` fresh + atomically single-use; `jti` unused; `iat`/`exp` inside a tight window (≤ nonce TTL); `aud` == this server; honor the PoP only on the **agent host** listener.

On success the connection is bound to the target **for its lifetime, subject to revocation/TTL (§9)**.

**gRPC (per-request):** DPoP header per call carrying `{ cnf, nonce, iat, exp, jti, aud, srv, htm, htu }` (note: `aud`+`srv` added here too). `htu` is the **canonical EXTERNAL origin** (pinned edge host + `https` + gRPC method path, host-lowercased, default-port-stripped) — the server computes the expected `htu` from a **configured external base URL**, never from `HttpContext.Request.Host/Scheme` (those are the h2c-internal values). Never relax `htm`/`htu`. **Streaming RPCs** are treated like a SignalR connection (bound at open, max-lifetime TTL, re-validation, cancel-on-revoke) — "per-request" does not cover stream bodies.

**`srv` claim** is the server-leaf thumbprint the agent observed; the server checks it equals its own. An attacker minting a token can copy the right value, so `srv` is **defence-in-depth / alerting only** — the agent-side server pin (§2) is the actual MITM guard.

## 6. Agent key storage & machine identity

- **Private key (non-exportable):** Windows → CNG machine-store / DPAPI, TPM-backed where available; Linux → `0600` key file under the data dir, TPM where available. Never exported, never plaintext.
- **Server pin out-of-band:** the server leaf-cert thumbprint is provisioned to the agent (config/MDM) **before** enroll, so the agent can verify the enroll endpoint and detect an enroll-time MITM rather than learning trust during the vulnerable exchange.
- **Machine fingerprint:** a stable id persisted separately from `agent.json`, used only as a re-attach **hint** (never an identity selector — see §4-H).

## 7. Hardening checklist (red-team-driven)

| # | Risk (lens) | Sev | Mitigation (where) |
|---|---|---|---|
| 1 | Verify against header JWK + trust `cnf` string (DPoP footgun) | **Crit** | Recompute thumbprint → membership check → *then* verify; `cnf`==recomputed (§5) |
| 2 | Re-attach takeover via `(fingerprint,name)` + API key | **Crit** | Re-attach requires PoP of existing key; hints not selectors (§4-H) |
| 3 | Enroll key not account-scoped (SaaS cross-account pin) | **Crit** | Key bound to one account; enforced server-side (§2, §4-H) |
| 4 | Nonce/jti single-use not atomic across HA nodes | High | Shared strongly-consistent store; atomic consume (§8) |
| 5 | Reconnect/resume replay of a connect-proof | High | Fresh nonce on *every* (re)connect; no stateful re-bind (§5, §8) |
| 6 | Revoke/rotation doesn't kill live connections | High | Track connection→thumbprint; abort on drop; max-bind TTL; periodic re-validate (§9) |
| 7 | alg=none / HS256 confusion / legacy-stack reuse | High | Pin `ES256`; modern `JsonWebTokenHandler`; isolate from HS256 `AgentJwt` (§5) |
| 8 | Downgrade to legacy 365-day query-string `AgentJwt` | High | Refuse legacy bearer on agent endpoints once Route B on; no creds in URL (§11) |
| 9 | Thumbprint not globally unique → ambiguous account | High | Global unique index; fail-closed lookup (§4-H) |
| 10 | Enroll-time TOFU MITM / rogue box claims roles | High | Out-of-band server pin; authz behind first-enroll approval (§4-H, §6) |
| 11 | Internal h2c hop sniff/inject | High | Protect Caddy↔app hop; widened threat model (§3, §9-edge) |
| 12 | gRPC streaming = auth-once like SignalR | Med | Classify RPCs; bind streams at open + TTL + cancel (§5) |
| 13 | `htu` canonicalization behind proxy | Med | External canonical `htu` from configured base URL; never relax (§5) |
| 14 | Rotation-overlap escalation (old-key connection survives) | Med | Per-thumbprint connection kill on drop (§9) |
| 15 | Nonce store DoS / eviction-forced replay | Med | Stateless HMAC nonce; populate single-use set on first valid PoP; rate-limit (§8) |
| 16 | RFC7638 canonicalization bugs / non-constant-time compare | Med | Tested impl, required members only, fixed-length coords, `FixedTimeEquals`, pin EC P-256, KAT vectors (§8) |
| 17 | Cross-transport jti namespace seam | Low | One jti namespace `(thumbprint,jti)` shared SignalR+gRPC; ≥128-bit (§8) |
| 18 | Backend connection coalescing cross-identity | Low | Bind identity to logical ConnectionId / HTTP-2 stream, never backend socket; regression test (§9) |
| 19 | Auth-failure enumeration oracle | Low | Uniform 401, no distinguishing detail (§5) |

Confirmed **already mitigated** by the base design (no change): cross-endpoint `htm`/`htu` repurposing; naive cut-and-paste replay (fresh nonce); shared `aud` in SaaS (identity is `cnf`, not `aud`); kid-confusion (identity = recomputed thumbprint).

## 8. Nonce / jti / freshness service

- **Stateless issuance:** server-HMAC'd opaque nonce over `{timestamp, connection-salt, server-key}` — store nothing at issue, validate by recomputing HMAC + TTL. Defeats nonce-store DoS.
- **Atomic single-use on validation:** populate a shared, strongly-consistent set **only on first successful PoP** — Redis `GETDEL`/Lua or Postgres single-shot `UPDATE … WHERE not_consumed RETURNING`; reject if 0 rows affected. One jti namespace keyed `(target-thumbprint, jti)`, ≥128-bit, native-TTL eviction covering the full nonce window (reject-don't-evict under pressure). **On-prem single-host** may use an in-process store — **topology-config-gated** so SaaS never silently runs node-local.
- Tight nonce TTL (single-digit seconds); per-target issuance rate limits; constant-time comparisons via `CryptographicOperations.FixedTimeEquals`.

## 9. Rotation, revocation & connection lifecycle

- **Connection binding is revocable, not just the credential.** Track the `ConnectionId`/stream set **per pinned thumbprint** (not just per target). On revoke / target-disable / rotation-drop, **actively abort** all live connections + gRPC streams bound to that thumbprint (`Context.Abort()`, stream cancellation). Add a hard **max-bind TTL** (re-prove after it) and periodic re-validation of the bound thumbprint against the live set.
- **Rotate (`update-trust` equivalent):** new keypair → `POST /api/agents/rotate` authed by the **current** key; `trusted_thumbprints` is a set with an overlap window; dropping the old thumbprint kills connections bound to it.
- **Edge:** Caddy plays no part in auth (no `client_auth`, no forwarded headers). Optionally a dedicated `agents.<domain>` host for routing/timeouts only. The internal hop must be protected (§3). Identity binding is keyed to the **logical** ConnectionId/HTTP-2 stream, never the backend transport socket (guards Caddy connection pooling).

## 10. Data model (additions)

- `api_keys` — `Id, PrincipalId, PrincipalKind, AccountId(scope), Hash, Prefix, Scopes, CreatedUtc, ExpiresUtc?, LastUsedUtc, RevokedUtc?`.
- `deployment_targets` — `TrustedThumbprints (set, globally-unique index), MachineFingerprint, Status(+PendingApproval)`.
- runtime: `connection → thumbprint → target` map for per-thumbprint connection kill; shared nonce/jti store (SaaS).

## 11. Migration

- Deprecate `Server:RegistrationToken` + `/api/agents/register` + `RegistrationTokenExpiryJob`.
- **Once Route B is enabled, the hub + gRPC must REFUSE the legacy HS256 `AgentJwt`** (don't merely stop issuing it) and must not accept agent credentials in the query string. Integration test: a valid legacy `AgentJwt` is rejected on `/hubs/agent`.
- Offline-drop + cloud (Azure/AWS) targets stay **pre-entered** — unaffected.

## 12. Install UX (Spectre.Console)

- **Silent:** `kraken-agent install --server <url> --api-key <key> --name <n> --roles web,db --tenants acme,globex --service`.
- **Interactive (TUI):** prompt URL + key → call `enroll/options` → `MultiSelectionPrompt` for roles/tenants (capped by key) → confirm → register service/daemon.
- Both register the OS service (`UseWindowsService` / systemd) and run the same `enroll`.

## A1. Rejected alternative — Route A (TLS-layer mTLS)

Agent presents a real client cert; Caddy runs `client_auth mode require` and forwards `{http.request.tls.client.fingerprint}`; app validates the thumbprint. Rejected because: it puts a **forgeable forwarded header** in the trust path (must be stripped/proxy-trusted), needs a **dedicated cert-terminating host** (can't require client certs on the browser wildcard host — Caddy #4696), and is fragile across the proxy (placeholder regressions, e.g. #5551). Route B achieves the same key-possession + thumbprint-pin guarantees with the edge kept dumb. Kept on record only as a fallback if app-layer PoP proves impractical.

## 13. Open questions

1. Nonce/jti store: Redis vs Postgres single-shot for SaaS; in-proc threshold for on-prem.
2. Key algorithm to enroll (EC P-256 / `ES256` assumed) and TPM mandatory vs best-effort.
3. Internal-hop protection mechanism (internal TLS vs UDS vs firewalled loopback).
4. Max-bind TTL + re-validation cadence values.
5. Per-agent vs shared key policy defaults per environment (prod = per-agent?).
6. First-enroll approval UX for prod targets.

## 14. References

- DPoP — RFC 9449. JWK Thumbprint — RFC 7638. Channel binding — RFC 9266.
- ASP.NET Core certificate/JWT auth; `Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler`.
- KrakenDeploy SaaS model — [`saas-multi-account-architecture.md`](saas-multi-account-architecture.md); edge — [`deploy/caddy`](../deploy/caddy).
- Caddy `tls`/`client_auth` (Route A only) — https://caddyserver.com/docs/caddyfile/directives/tls

## 15. History

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-16 | DJ / Claude Code | Initial draft: API-key enroll-only + cert-thumbprint mutual auth; Caddy SaaS edge |
| 0.2 | 2026-06-16 | DJ / Claude Code | Route B (app-layer DPoP PoP) made primary; Route A → rejected alternative; folded 5-lens red-team (3 Crit / 9 High) into §4-H, §5, §7–§9, §11; widened threat model to the internal hop |

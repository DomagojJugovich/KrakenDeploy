# Agent Transport & Offline Trust Hardening

| | |
|---|---|
| **Version** | 1.1 |
| **Date** | 2026-07-15 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, JWT (HS256), SignalR, gRPC (HTTP/2), DPAPI, Serilog |
| **Projects** | KrakenDeploy.Server, KrakenDeploy.Server.Data, KrakenDeploy.Server.Transport, KrakenDeploy.Agent, KrakenDeploy.Agent.Transport, KrakenDeploy.Contracts |

| Version | Change |
|---|---|
| 1.0 | A8 initial: revocation, 90 d lifetime, iss/aud, cleartext gating, agent.json at rest, redaction, offline fail-closed. |
| 1.1 | Sliding token auto-refresh (§1a) + configurable lifetime — routine re-enrollment eliminated. |

## Purpose

Covers the A8 hardening batch for the agent trust boundary: revocable, short-lived
agent tokens with issuer/audience enforcement (T1-12), cleartext-transport gating
and at-rest protection of the agent's credential, and a fail-closed offline-result
channel (T1-15).

## 1. Agent token: revocation, lifetime, iss/aud (T1-12)

The agent bearer token is an HS256 JWT signed with the shared `Agent:JwtSigningKey`.
Its `sub` (`ClaimTypes.NameIdentifier`) carries the target id, which every hub
callback and gRPC service reads back to resolve the target row. Before A8 the token
lived **365 days**, `ValidateIssuer`/`ValidateAudience` were **off**, and the only
way to revoke it was to delete the target (rotating the signing key would drop the
entire fleet).

### Revocation — per-target token version

`DeploymentTarget.AgentTokenVersion` (column `agent_token_version`, default `0`) is
stamped into the token as the `atv` claim (`AgentTokenClaims.TokenVersion`, defined
in `KrakenDeploy.Contracts` so the issuer and validator share one source of truth).
On every authenticated agent request the `AgentJwt` scheme's `OnTokenValidated`
event calls `AgentTokenValidator.ValidateAsync`, which compares the claim to the
target's current version against the database and **fails closed**:

- missing/garbled `sub` or `atv` → reject,
- target no longer exists (or resolves to the wrong tenant DB in multi-account) → reject,
- version mismatch (revoked) → reject.

This runs **once per SignalR connect** and **once per gRPC call**. Revocation is one
operator action — `TargetService.RevokeAgentTokenAsync` bumps the version
atomically, and `AgentAccessRevoker` also drops the live tunnel immediately
(`IAgentConnectionRegistry.AbortConnectionFor`, backed by the hub's `Context.Abort`
captured at connect) and writes an `Agent.TokenRevoked` audit row. Surface:
`POST /api/targets/{id}/revoke-agent-token` (`Permission.MachineEdit`) and a **Revoke
agent access** button on the target Connectivity tab (Reverse targets). The agent
must re-enroll afterwards.

> Deliberate scope decision: role changes do **not** bump the version (RBAC is
> live-resolved on every action).

### iss/aud + lifetime

`ValidateIssuer`/`ValidateAudience` are now **true** (every issued token already
carried `iss=KrakenDeploy` / `aud=KrakenDeploy.Agent`), and `ValidateLifetime` is
pinned explicitly so a future default change can't silently disable expiry. Lifetime
defaults to **90 days** and is configurable via `Agent:TokenLifetimeDays` (server
side, ≥ 1 enforced at startup). With auto-refresh (§1a) the lifetime is **not** an
operator chore interval — it is the *maximum tolerated offline gap* before a manual
re-enroll, and the window in which a token that has stopped refreshing (dead or
decommissioned box) silently ages out of trust.

## 1a. Sliding token auto-refresh

A fixed lifetime without renewal would mean a fleet-wide manual re-enroll every
90 days (~150 targets = a real operational chore), and a 5-year token was rejected
as a long-lived bearer credential riding every WebSocket handshake. Instead the
agent **renews its own token** — chosen fork: *auto-refresh, no rotation*.

- **Server** — `POST /api/agents/refresh-token`, authenticated by the **current**
  agent token under an AgentJwt-only policy (an API key cannot mint agent tokens).
  The scheme's `OnTokenValidated` has already run the `atv` revocation check, so a
  **revoked or expired token cannot refresh** — revocation cannot be outrun by
  renewing. The handler re-runs `AgentTokenValidator` and stamps the new token with
  the **claim's** version, so a revoke racing the refresh yields a dead-on-arrival
  token, never a laundered fresh one. Every refresh writes an
  `Agent.TokenRefreshed` audit row (forensic trail; an anomaly spike can indicate a
  stolen token being kept alive).
- **Agent** — `TokenRefreshHostedService` checks the token's own `nbf`/`exp` (no
  server round-trip) every 6 h and immediately on boot; once past **half** of the
  validity window it calls the endpoint, persists the new identity to `agent.json`
  **first**, then swaps it into `AgentContext`. The SignalR `AccessTokenProvider`
  and the gRPC token accessors resolve the token **lazily**, so reconnects and new
  channels pick up the fresh token without a restart.
- **No rotation (deliberate)** — the old token stays valid until its own `exp`.
  Rotation (bump `atv` per refresh) would give single-live-token semantics and
  theft detection, but its crash window (server bumped, `agent.json` not yet
  persisted) bricks agents — reintroducing random manual re-enrolls. With no
  rotation there is no failure window at all. Consequence to be aware of: a stolen
  token *copy* remains valid until its `exp` even after the legitimate agent
  refreshed — the answer to a **known** leak remains revocation, which kills the
  stolen copy and every refresh attempt instantly.

Net effect: an agent that is online at least once per half-lifetime (45 d at
defaults) **never needs re-enrollment**. Re-enrollment remains only for: a revoked
agent, an agent offline longer than the full lifetime, or an undecryptable
`agent.json` (§3).

## 2. Cleartext HTTP/2 gating + https enforcement (T1-12)

The three gRPC clients each set the process-global
`Http2UnencryptedSupport` switch **unconditionally**, and the server URL was used
verbatim — so a production agent could silently speak cleartext h2c and send its
token in the clear.

- `GrpcChannelFactory` is now the single channel-construction point. It enables the
  cleartext switch **only** for an `http://` URL under an explicit override, and
  never for `https`.
- `AgentTransportSecurity.Validate` is the policy: **https required**; `http://`
  refused unless `Server:AllowInsecureHttp = true` (dev-only). One URL backs both
  the SignalR tunnel and the gRPC channels, so this covers every transport.
- `RegistrationHostedService` **fails fast at startup** on a cleartext URL — both the
  configured `Server:Url` and a URL persisted in an older `agent.json`.

```jsonc
// agent appsettings — local development ONLY
"Server": { "Url": "http://localhost:5000", "AllowInsecureHttp": true }
```

## 3. agent.json at rest (T1-12)

`agent.json` holds the long-lived token. It was plaintext with the default
`%ProgramData%` ACL on Windows (any local user could read it); Unix already used
chmod-600.

- **Windows** — the content is DPAPI-encrypted (`DataProtectionScope.LocalMachine`,
  magic-prefixed `KDPAPIv1`) and the data directory's ACL is tightened via `icacls`
  (`/inheritance:r` + grant SYSTEM + Administrators + the service account by
  well-known SID). LocalMachine DPAPI is decryptable by any local process, so the
  ACL is the on-box confidentiality control; `icacls` avoids the deprecated
  `System.IO.FileSystem.AccessControl` package. ACL hardening is best-effort
  (logged, non-fatal — the content stays DPAPI-encrypted regardless).
- A legacy plaintext `agent.json` is **auto-migrated** to the protected form on first
  read; an undecryptable blob (e.g. copied to another machine) fails closed to
  "unenrolled" → clean re-enroll.
- **Unix** — chmod-600 unchanged.

> The DPAPI API is Windows-only and is always called under
> `OperatingSystem.IsWindows()`. `System.Security.Cryptography.ProtectedData` is the
> only added dependency.

## 4. access_token log redaction (T1-12, defense-in-depth)

SignalR delivers the token as `?access_token=` (WebSocket upgrades can't carry
headers). Serilog's request logger excludes the query by default and the framework's
URL-with-query line is suppressed, so there is **no active leak today** — but a
single flag flip would expose it. `RequestLogRedaction` + a custom
`GetMessageTemplateProperties` rebuild the `RequestPath` property with any
`access_token` value replaced by `REDACTED`, so the token can never surface in the
request log regardless of those flags.

## 5. Offline result: fail closed (T1-15)

An offline-drop result bundle returns over an **untrusted** channel (file share,
email, manual upload) and drives status, step-outcome and output-variable DB writes.
`OfflineResultService.IngestAsync` skipped signature verification when no per-target
bundle key was configured or the target didn't resolve, and a bundle that omitted
`deployment-result.json` hit a "succeeded by convention" path that skipped
verification entirely. Now:

- no resolved target → reject (no key to verify against),
- offline-drop target with no bundle key → reject (outbound dispatch already requires
  the key, so a legitimately dispatched drop always has one),
- `deployment-result.json` **and** `result-signature.bin` are mandatory and always
  verified (`OfflineResultSigner`, `FixedTimeEquals`).

**Known residual gaps** (out of A8 scope): the manifest HMAC remains optional, and
`deployment-log.txt` + `artifacts/` are unsigned even in the keyed path.

## Configuration reference

| Key | Default | Meaning |
|---|---|---|
| `Agent:JwtSigningKey` (server) | — (required) | HS256 signing key (≥32 bytes). |
| `Agent:TokenLifetimeDays` (server) | `90` | Agent token lifetime; with auto-refresh (§1a) this is the maximum tolerated offline gap, not a chore interval. ≥ 1. |
| `Server:Url` (agent) | — | Server base URL; **https required**. |
| `Server:AllowInsecureHttp` (agent) | `false` | Dev-only override allowing an `http://` URL + cleartext h2c. |
| `Agent:DataPath` (agent) | OS default | Directory holding the DPAPI-protected `agent.json`. |

## Operational notes

### Re-enrollment is manual — and, with auto-refresh, exceptional

An agent registers **only when it has no stored identity**: on startup
`RegistrationHostedService` loads `agent.json` and, if an identity is present, uses
that token and skips registration. An agent whose token the server rejects
(revoked, offline past the full lifetime, pre-A8 token without `atv`) does **not**
self-re-enroll — it retries the connection and keeps getting `401`. Routine expiry
is handled by the sliding refresh (§1a), so under normal operation re-enrollment
never recurs; when it *is* needed, it is an operator action:

1. Generate a fresh one-time registration token in the Targets UI for the target.
2. On the agent host, delete `agent.json` from the data directory
   (`Agent:DataPath`, default `%ProgramData%\KrakenDeploy\Agent` on Windows /
   `/var/lib/krakendeploy-agent` on Unix).
3. Restart the agent with the new token
   (`--Server:RegistrationToken=<token>` or in `appsettings.json`).

The agent then registers again and receives a token carrying `atv`. (A self-heal
path isn't available anyway: the one-time token is consumed server-side after first
use, so a running agent has nothing to re-register with.)

> The DPAPI **content** migration (plaintext `agent.json` → protected) *is*
> automatic on first read — that is separate from token re-enrollment, which turns
> on the token's *claims*, not its at-rest encoding.

- **Revoking an agent**: target → Connectivity → *Revoke agent access* (or
  `POST /api/targets/{id}/revoke-agent-token`). The live tunnel drops immediately
  and every outstanding token is rejected. The lockout is intended to persist until
  an operator re-enrolls the agent (steps above) — a kill switch that auto-healed
  would defeat the purpose.
- **Upgrade from a pre-A8 agent**: existing tokens predate the `atv` claim, so
  enabling this enforcement breaks already-enrolled agents until they are re-enrolled
  (steps above). Pre-production, so there is no live fleet — but any dev/test agent
  already enrolled must be re-enrolled after this upgrade.
- **Windows service account**: DPAPI uses LocalMachine scope, so an account change
  does not strand `agent.json`; the `icacls` grant targets the running account, so
  re-run enrollment (or re-save) after changing the service identity.

## References

- `docs/production-readiness-audit-2026-07-13.md` — T1-12, T1-15.
- `docs/auth-session-hardening.md` — A7 (session revocation model this mirrors).
- [DataProtectionScope](https://learn.microsoft.com/dotnet/api/system.security.cryptography.dataprotectionscope)
- [JwtBearerEvents.OnTokenValidated](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.jwtbearer.jwtbearerevents)

# Agent Transport & Offline Trust Hardening

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-15 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, JWT (HS256), SignalR, gRPC (HTTP/2), DPAPI, Serilog |
| **Projects** | KrakenDeploy.Server, KrakenDeploy.Server.Data, KrakenDeploy.Server.Transport, KrakenDeploy.Agent, KrakenDeploy.Agent.Transport, KrakenDeploy.Contracts |

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
> live-resolved on every action). The chosen posture is **90-day lifetime +
> revocation, no refresh subsystem** — revocation gives an instant kill for a known
> leak, at-rest protection (§3) closes the primary leak vector, and the shortened
> lifetime bounds an unknown leak. A refresh flow can be added later if a shorter
> lifetime is wanted.

### iss/aud + lifetime

`ValidateIssuer`/`ValidateAudience` are now **true** (every issued token already
carried `iss=KrakenDeploy` / `aud=KrakenDeploy.Agent`), and `ValidateLifetime` is
pinned explicitly so a future default change can't silently disable expiry. Lifetime
is **90 days** (`AgentJwtService.TokenLifetime`).

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
| `Server:Url` (agent) | — | Server base URL; **https required**. |
| `Server:AllowInsecureHttp` (agent) | `false` | Dev-only override allowing an `http://` URL + cleartext h2c. |
| `Agent:DataPath` (agent) | OS default | Directory holding the DPAPI-protected `agent.json`. |

## Operational notes

- **Revoking an agent**: target → Connectivity → *Revoke agent access* (or the REST
  endpoint). The live tunnel drops immediately; re-enroll the agent with a fresh
  registration token.
- **Upgrade**: agents must re-enroll to obtain a token carrying the `atv` claim (the
  old validation was permissive; enforcement is now on). Pre-production, so no live
  fleet is affected.
- **Windows service account**: DPAPI uses LocalMachine scope, so an account change
  does not strand `agent.json`; the `icacls` grant targets the running account, so
  re-run enrollment (or re-save) after changing the service identity.

## References

- `docs/production-readiness-audit-2026-07-13.md` — T1-12, T1-15.
- `docs/auth-session-hardening.md` — A7 (session revocation model this mirrors).
- [DataProtectionScope](https://learn.microsoft.com/dotnet/api/system.security.cryptography.dataprotectionscope)
- [JwtBearerEvents.OnTokenValidated](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.authentication.jwtbearer.jwtbearerevents)

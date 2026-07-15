# SSRF Hardening & Outbound-URL Policy

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-15 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, `SocketsHttpHandler`, `Microsoft.Extensions.Options` |
| **Projects** | KrakenDeploy.Server, KrakenDeploy.Server.Data |

## Purpose

KrakenDeploy makes several outbound HTTP calls to operator- or upstream-supplied
URLs. On a segmented government network an unfiltered outbound call is an
internal-network probe / cloud-metadata-exfil primitive. This document describes
the guard that constrains those calls and the `Ssrf` configuration section that
tunes it.

## Threat model & guarded call sites

| Integration | URL origin | Trust | Enforcement |
|---|---|---|---|
| Webhook delivery | subscription config | operator | pre-flight + pinning handler, redirects **off** |
| Step catalog (package + template) | upstream GitHub release JSON (`browser_download_url`) | upstream (attacker-influenceable) | pre-flight + pinning handler, redirects **on** |
| OIDC Authority | identity-provider config | admin | save-time + backchannel pinning handler |
| AI endpoint (`BaseUrl`) | Space AI settings | admin | save-time + per-call pre-flight |

## Address classification

Every candidate IP is classified in one of three tiers (`SsrfGuard`):

- **Hard-blocked, never allowlistable** — link-local / cloud-metadata
  (`169.254.0.0/16` incl. `169.254.169.254`, `fe80::/10`) and the unspecified
  address (`0.0.0.0` / `::`). Re-enabling these would defeat the guard.
- **Policy-gated** — loopback (`127.0.0.0/8`, `::1`) and private ranges
  (RFC1918, CGNAT `100.64.0.0/10`, IPv6 ULA `fc00::/7`). Denied by default;
  opt in per integration.
- **Public** — always allowed.

## Two enforcement layers

1. **Pre-flight** — `SsrfGuard.ValidateOutboundUrlAsync(url, policy, ct)` resolves
   the host and refuses out-of-policy URLs with a clear reason before any socket
   opens.
2. **Connection pinning** — `SsrfHttpHandlerFactory.Create(policy, allowAutoRedirect)`
   builds a `SocketsHttpHandler` whose `ConnectCallback` re-validates the resolved
   address and connects to (pins) that exact IP on **every** connection — the
   initial request and each redirect hop. This closes two gaps a pre-flight check
   alone cannot: the redirect bypass (a `302` to an internal host) and the
   DNS-rebind TOCTOU (the name is not re-resolved between check and connect).
   TLS/SNI still uses the request hostname, so certificate validation is intact.

## Configuration — the `Ssrf` section

One `SsrfPolicy` per integration. Defaults are **deny** for loopback and private
ranges (link-local/metadata is always denied); `Ai` defaults `AllowLoopback: true`
so a co-resident local model server (Ollama / LM Studio on `127.0.0.1`) works out
of the box. Omitting the section leaves all secure defaults in place.

```json
{
  "Ssrf": {
    "Webhook":     { "AllowLoopback": false, "AllowPrivate": false, "AllowedHosts": [] },
    "StepCatalog": { "AllowLoopback": false, "AllowPrivate": false, "AllowedHosts": [] },
    "Oidc":        { "AllowLoopback": false, "AllowPrivate": false, "AllowedHosts": [] },
    "Ai":          { "AllowLoopback": true,  "AllowPrivate": false, "AllowedHosts": [] }
  }
}
```

- `AllowLoopback` — permit `127.0.0.0/8` and `::1`.
- `AllowPrivate` — permit RFC1918 / CGNAT / IPv6 ULA in bulk.
- `AllowedHosts` — explicit allowlist entries. Each is a hostname (matched
  case-insensitively against the request host), a literal IP, or a CIDR block
  (matched against the resolved address). A match bypasses the loopback/private
  denials but **not** the hard block on link-local/metadata/unspecified. Prefer
  this over opening a whole range: to reach one internal webhook receiver, list
  its host or `/32`, not `AllowPrivate: true`.

Example — allow webhook delivery to two internal receivers only:

```json
"Webhook": { "AllowedHosts": [ "hooks.intranet.example", "10.20.30.0/24" ] }
```

## Deployment note (behaviour change)

Before this change RFC1918 was allowed by design. It is now **deny-by-default per
integration**. On-prem operators that deliver webhooks to internal receivers must
allowlist those hosts (`Ssrf:Webhook:AllowedHosts`) or set
`Ssrf:Webhook:AllowPrivate: true`. This is a configuration change only — no wire,
EF, or REST contract is affected.

## Residual

The AI provider SDKs (`OpenAI` / `Anthropic`) construct their own HTTP transport,
which KrakenDeploy does not own, so AI endpoints are guarded at save-time and on
every settings read but the SDK transport itself is not IP-pinned (it may still
follow a redirect or re-resolve DNS). AI is admin-configured and the lowest-risk
site; closing this fully requires SDK-transport injection and is deferred.

## References

- `src/KrakenDeploy.Server.Data/Net/SsrfGuard.cs`
- `src/KrakenDeploy.Server.Data/Net/SsrfPolicy.cs`
- `src/KrakenDeploy.Server.Data/Net/SsrfHttpHandlerFactory.cs`
- [SocketsHttpHandler.ConnectCallback](https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.connectcallback)
- [RFC 1918 — Private Address Space](https://datatracker.ietf.org/doc/html/rfc1918), [RFC 6598 — CGNAT](https://datatracker.ietf.org/doc/html/rfc6598)

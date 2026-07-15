# Authentication & Session Hardening

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-15 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, ASP.NET Core Identity, Blazor Server, DataProtection, ForwardedHeaders |
| **Projects** | KrakenDeploy.Server, KrakenDeploy.Server.Data |

## Purpose

Covers the A7 hardening batch for the authentication/session layer: live session
revocation (T1-13), encrypting the DataProtection key ring at rest (T1-14), and
correct cookie `Secure` + client-IP handling behind a TLS-terminating proxy (M2).

## 1. Session revocation (T1-13)

Before A7, the 7-day sliding auth cookie was self-validating and the Blazor
circuit captured the principal once — a password reset or offboard could not
terminate an existing session or circuit. Two mechanisms now revalidate the
principal against the database:

- **Cookie** — `SecurityStampValidator` is wired onto the auth cookie's
  `OnValidatePrincipal`; a request whose principal carries a stale security stamp
  is rejected and signed out.
- **Circuit** — `RevalidatingIdentityAuthenticationStateProvider` re-checks the
  live Blazor circuit's principal (security stamp **and** the `IsDisabled` flag),
  tearing the circuit's auth down when either fails.

Both run on the same interval, `Auth:SessionRevalidationMinutes` (default **15**).
Because the cookie uses sliding expiration, this interval — not `ExpireTimeSpan` —
is the effective **revocation latency**: a change takes effect within one interval.

### What triggers revocation

| Action | Effect |
|---|---|
| Admin **password reset** (`UserService.ResetPasswordAsync`) | Replaces the hash → Identity bumps the security stamp → the user's *other* sessions are rejected within the interval. |
| Admin **disable** (`UserService.SetDisabledAsync`) | Sets `IsDisabled` (persistent sign-in gate) **and** bumps the stamp → live sessions/circuits revoked within the interval. |
| **Role change** | *No* stamp bump — RBAC is live-resolved on every action (`UiActionGuard`, `bypassCache`), so role changes already apply immediately. A stamp bump would be redundant. |

`IsDisabled` is also enforced at sign-in: the password path (`Login.razor`) and the
OIDC path (`OidcRegistrar`, before `SignInAsync`) both refuse a disabled account.

Operators manage this from **Configuration → Users**: per-row **disable/enable**
(`Permission.UserEdit`) and **reset password** (`Permission.UserChangePassword`,
human accounts only; the temp password is shown once to convey securely).

### Config

```jsonc
"Auth": { "SessionRevalidationMinutes": 15 }   // optional; lower = faster revocation, more DB checks
```

## 2. DataProtection key-ring encryption (T1-14)

The key ring protects auth + antiforgery cookies. If it is readable in plaintext,
an attacker who can read the directory can forge those cookies.

- **Windows** — encrypted at rest with **DPAPI** (`ProtectKeysWithDpapi`), unchanged.
- **Non-Windows / HA** — encrypted with an **X.509 certificate**
  (`ProtectKeysWithCertificate`). Configure a PFX:

  ```jsonc
  "DataProtection": {
    "CertificatePath": "/etc/krakendeploy/dp-cert.pfx",
    "CertificatePassword": "…"            // env var form: DataProtection__CertificatePassword
  }
  ```

  The same certificate must be present on every HA node (so nodes can read a shared
  ring). Source it from your KMS/HSM as an exported PFX. **Fail-fast:** in a
  non-Development environment on a non-Windows host with no certificate configured,
  the server **refuses to boot** rather than silently writing a plaintext ring
  (Development warns and continues).

- **Ring location** — the ring now defaults under `Server:DataPath`
  (`{DataPath}/dataprotection-keys`), so it lands on the mounted, backed-up volume
  and `BackupEngine` captures it. Override with `DataProtection:KeyPath`.
  **Upgrade note:** deployments that set `Server:DataPath` will see the ring move
  from the old relative `./data` location — a one-time relocation that signs
  existing sessions out once.

### Required directory ACLs (Linux)

Even encrypted, restrict the key directory to the service account:

```bash
install -d -m 0700 -o kraken -g kraken /data/dataprotection-keys
# verify: only the service user can read/traverse
stat -c '%A %U:%G' /data/dataprotection-keys      # expect: drwx------ kraken:kraken
```

The certificate PFX must be equally protected (`chmod 0600`, owned by the service
user). On Windows the DPAPI-encrypted ring is bound to the machine/user; still
restrict the directory to the app-pool identity.

## 3. Cookie Secure + forwarded headers (M2)

The app runs behind a TLS-terminating edge (**Caddy**; the blue-green **Router**
forwards plain HTTP and passes headers through). Without care the app sees HTTP and
drops the cookie `Secure` attribute and records the proxy IP.

- **Cookie `Secure`** — `CookieSecurePolicy.Always` outside Development on both the
  application and the external/OIDC interim cookie, so `Secure` is set regardless of
  the perceived scheme. (Development keeps `SameAsRequest` for HTTP smoke tests.)
- **Forwarded headers** — `UseForwardedHeaders` runs first in the pipeline, honoring
  `X-Forwarded-Proto`/`X-Forwarded-For`, so `Request.IsHttps` and `RemoteIpAddress`
  reflect the real client (this also sharpens the agent-register rate-limit
  partition and audit source IP).

  ```jsonc
  "ForwardedHeaders": {
    "KnownProxies":  [ "172.18.0.2" ],        // exact edge-proxy IP(s), e.g. the Caddy container
    "KnownNetworks": [ "172.18.0.0/16" ],     // OR the proxy subnet (Docker bridge); CIDR
    "ForwardLimit":  1                          // one trusted hop (Caddy)
  }
  ```

  **Important (net10):** forwarded headers from a proxy **not** listed here are
  **ignored** (the default trusts loopback only). The blue-green Router forwards
  over loopback and is covered by the default, but the shipped single-host
  Caddy→container topology arrives from a **non-loopback Docker-bridge IP** — you
  **must** set `KnownProxies` (the Caddy IP) or `KnownNetworks` (the bridge subnet)
  or forwarded headers are dropped. Do **not** set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`
  (it trusts any source → spoofable `X-Forwarded-For`). `CookieSecurePolicy.Always`
  is complementary, so the auth cookie is `Secure` even before this is configured.

## References

- `src/KrakenDeploy.Server/Auth/RevalidatingIdentityAuthenticationStateProvider.cs`
- `src/KrakenDeploy.Server/Program.cs` (auth cookie + SecurityStampValidator, DataProtection, ForwardedHeaders)
- `src/KrakenDeploy.Server.Data/Services/UserService.cs` (`SetDisabledAsync`, `ResetPasswordAsync`)
- [ASP.NET Core: configure to work with proxy servers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)
- [DataProtection: ProtectKeysWithCertificate](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview)
- [Identity: security stamp / SecurityStampValidator](https://learn.microsoft.com/aspnet/core/security/authentication/identity)

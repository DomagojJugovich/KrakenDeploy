# Production Hardening: DI validation, DB resiliency, deep readiness

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-16 |
| **Authors** | Domagoj Jugović (LAUS CC) — implemented with Claude Code |
| **Status** | `Review` |
| **Technologies** | .NET 10, ASP.NET Core, EF Core 10, Npgsql, PostgreSQL |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Data` |

C3 (audit items T1-18, T1-19, P1) closes three production-robustness gaps: the DI
container was validated only in Development, the Npgsql connection was configured
with no failover resiliency, and `/healthz` reported healthy while the server was
unable to actually serve a deployment.

## 1. DI validation in every environment (T1-18)

`WebApplication.CreateBuilder` turns `ValidateScopes` + `ValidateOnBuild` on in
**Development only**. A captive-dependency defect (a singleton capturing the
scoped `IDbContextFactory<KrakenDbContext>`) therefore aborted startup in Dev but
slipped to a first-resolution runtime failure in Production.

The web host now forces both on in all environments:

```csharp
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});
```

- `ValidateOnBuild` validates every registered descriptor at build. It does **not**
  validate middleware activation (conventional middleware is built once from the
  root provider), so those defects still surface at host start — the reason a boot
  check, not just a build, is the acceptance gate.
- `ValidateScopes` throws when a scoped service is resolved from the root scope.

The CLI host (`Commands/CliHost.cs`) deliberately keeps `ValidateOnBuild = false`
— CLI commands register only a subset of the graph and never start the web host.

## 2. DB connection resiliency (T1-19)

`UseNpgsql` was called bare — no retry strategy (an in-flight query hard-failed a
deployment on a transient Postgres blip / Patroni failover) and no pool cap.

New `Database` configuration section (defaults shown):

```json
"Database": {
  "EnableRetryOnFailure": true,
  "MaxRetryCount": 6,
  "MaxRetryDelaySeconds": 30,
  "MaxPoolSize": 50
}
```

- **Retry** installs `NpgsqlRetryingExecutionStrategy`. It is applied **web-host
  only**, not in CLI commands: the retrying strategy is incompatible with
  user-initiated transactions (EF throws *"does not support user-initiated
  transactions"*), and `encryption rotate-dek` / `rotate-kek` open one. The web
  host's tenant `KrakenDbContext` opens no user transaction, so retry is safe
  there. This is the same web-host/CLI split as `ValidateOnBuild`.
- **Pool cap** is applied to the connection string (`Maximum Pool Size` is an
  Npgsql keyword, not an EF option). An operator-supplied pool size in the
  connection string wins; `MaxPoolSize <= 0` disables the cap. See the
  [connection budget](ha-pair.md#database-connections) for the shared-Postgres HA
  math and the PgBouncer recommendation.

> Multi-account note: the per-account tenant connection is re-applied in
> `KrakenDbContext.OnConfiguring` without this resiliency config, so retry
> currently applies to single-instance only. That is fine while multi-account is
> fenced off (the host refuses to boot it), but the override must mirror this when
> per-account DEK lands.

## 3. Deep readiness — `/health/ready` (P1)

`/healthz` stays a shallow **liveness** probe (process up + DB reachable). The new
`/health/ready` is a **readiness** probe: it answers "can this instance serve a
deployment right now?" so an orchestrator / load balancer drains a node that is up
but degraded.

It probes three deployment prerequisites and returns `200`/`ready` or
`503`/`unready` with per-probe booleans and a sanitised `detail` (never key
material or ciphertext):

| Probe | What it checks | Why it matters |
|---|---|---|
| `database` | `CanConnectAsync` | No DB, no deployment. |
| `encryption` | encrypt→decrypt round-trip through the DEK | In Production the DEK is **not** eagerly loaded at boot (`EnsureDekAsync` is Development-only), so a wrong KEK / bricked DEK (C2) otherwise surfaces only at the first secret access mid-deployment. This forces the unwrap and reports unready. |
| `dataDirectory` | write-then-delete under `Server:DataPath` | Packages, offline drop bundles and the Data-Protection ring land here; an unwritable / full volume (T0-9) breaks deployments while the process stays up. |

`/health/ready` is registered as space-agnostic (`SpaceRouting.AgnosticPrefixes`
gets `/health`) — otherwise `SpaceUrlRedirectMiddleware` would 302 the probe into
`/s/default/health/ready`. Both health endpoints are anonymous.

Example unready response:

```json
{ "status": "unready", "database": true, "encryption": false,
  "dataDirectory": true, "detail": "encryption unavailable (CryptographicException)" }
```

## References

- [High-Availability Pair — connection budget](ha-pair.md#database-connections)
- [On-prem DEK + DataPath (C2)](on-prem-guide.md)
- EF Core connection resiliency: `Microsoft.EntityFrameworkCore` — `EnableRetryOnFailure`

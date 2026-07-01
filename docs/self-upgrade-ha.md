# KrakenDeploy — Self-Managed Multi-Node HA: Operations, Maintenance & Drain

| | |
|---|---|
| **Version** | 0.2 |
| **Date** | 2026-06-23 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude |
| **Status** | `Draft` |
| **Technologies** | .NET 10, Blazor Server, ASP.NET Core, EF Core 10, PostgreSQL, Hangfire, Caddy, YARP |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Data`, `KrakenDeploy.Server.Core` |
| **Applies to** | Self-managed multi-node HA installs **and** plain-VM cloud deployments (Hetzner, bare Droplets, any IaaS) where you operate the LB, Postgres, and rollout yourself |

## Revision history

| Version | Date | Author | Change |
|---|---|---|---|
| 0.1 | 2026-06-17 | DJ | Initial draft: self-managed counterpart to [`saas-multi-account-architecture.md`] §13. Two-class state model; topology prerequisites (Caddy cookie affinity + WebSocket + TLS, Caddy HA, shared Data Protection key ring, durable lease-claimed step ledger, drainable executor tier); self-managed Postgres HA & backup (replication/failover, pgBackRest PITR, pooling); expand/contract migrations; node-by-node rolling-upgrade runbook; circuit drain via Caddy health checks; bootstrap caveat. |
| 0.2 | 2026-06-23 | DJ | Reconciled with the **blue-green slot scheme** ([`blue-green-slot-deployment.md`]). Separated the two orthogonal operations: app-**version** upgrades use the slot flip (deploy to a spare slot, flip the default), while this doc's **node-by-node drain** is repurposed for **node/host maintenance** (OS patch, kernel, reboot, host-level breaking change) — drain a whole node and *all three of its slots* via Caddy. **Reversed the "separate executor tier"** prerequisite: the monolith (UI + orchestrator + Hangfire) ships as one versioned unit (D-bg-1); the durable step ledger, not a separate tier, is what makes a drain/loss safe. §6 runbook rewritten as a node-maintenance runbook. YARP added to the stack. **Retitled** (was "Self-Upgrade, Rolling Deploy & Drain") to reflect that app-version rollout now lives in the slot scheme, leaving this doc focused on self-managed HA operations + node maintenance/drain. |

---

## 1. Purpose & scope

How to roll out a new KrakenDeploy version across a **self-managed, multi-node HA install** without dropping in-flight deployments, and how to drain Blazor circuits gracefully. This applies equally to **plain-VM cloud deployments** — Hetzner, bare Droplets, any IaaS — where *you* operate the load balancer, Postgres, and the rollout. The DigitalOcean-managed SaaS path (DO Managed Postgres, DO Load Balancer, DOKS) is covered in [`saas-multi-account-architecture.md`] §13; this is the **you-do-everything** counterpart and pairs with [`ha-pair.md`].

Single-node installs need none of this: stop the service, migrate, start. Everything below is the cost of running ≥2 nodes.

> **Two orthogonal operations — don't conflate them.**
> - **(A) App-version upgrade** → the **blue-green slot flip** ([`blue-green-slot-deployment.md`]): deploy the new release to a spare slot, health-gate it, flip the default; the old release drains *across all nodes* and retires. This is **not** in this doc.
> - **(B) Node / host maintenance** (OS patch, kernel, reboot, hardware, a host-level breaking change) → the **node-by-node drain** in this doc (§6): take a whole node — and therefore *all three of its slots* — out of service via Caddy, service it, return it.
>
> They run on different axes: (A) drains one *release* across every node; (B) drains every *slot* on one node. (B) does **not** rely on any L4 load balancer — Caddy's health-check fallback is the drain. Both obey the same rule: never abandon or hard-kill an in-flight deployment (§2).

## 2. Two classes of state — not equally precious

A rolling upgrade puts two very different things at risk, and conflating them is the trap:

| State | On a slot/node drain | Criticality |
|---|---|---|
| **Blazor circuit** (a user's UI session — the SignalR connection + server-side render tree) | Reconnects to a peer node, where YARP routes the same `kd_ver` to the same release | **Recoverable.** Cosmetic. `.NET 10` circuit state persistence makes it near-seamless. |
| **In-flight deployment** (a running task pushing a release to a target) | Half-applied changes on a production machine | **Irreplaceable, side-effecting.** Must never be abandoned or double-run. |

Everything below follows from this ordering: **protect running deployments absolutely; treat circuits as best-effort.**

## 3. What your topology must provide

A rolling upgrade is only *possible* if the install already has:

- **≥2 app nodes behind a front that does cookie session affinity + WebSocket + TLS.** Use Caddy (your front everywhere) with `lb_policy cookie`:

  ```caddyfile
  app.internal {
      reverse_proxy app1:8080 app2:8080 app3:8080 {
          lb_policy cookie kraken_aff <shared-secret> {
              fallback least_conn
          }
          flush_interval -1          # stream WebSocket/SignalR; no buffering
          health_uri      /healthz   # active checks drive drain (below)
          health_interval 10s
          health_timeout  5s
      }
  }
  ```

  A fixed `<shared-secret>` makes the cookie→node map stable across Caddy restarts, so a Caddy bounce reconnects clients to the *same* node and the circuit resumes inside Blazor's disconnected window.

- **HA for Caddy itself.** Caddy is now the front-line single point of failure. Either two Caddy nodes sharing a floating/virtual IP via `keepalived` (VRRP), or a small L4 (TCP) load balancer in front of them. With identical config + identical secret, both Caddy nodes map cookies identically, so failover is seamless.

- **A shared Data Protection key ring across all app nodes** — persisted to the database or a shared path (`PersistKeysToDbContext`, or a mounted share), so antiforgery tokens and the `__Host-` session cookie issued by one node validate on another. Without it, every request that crosses nodes breaks auth.

- **Durable, resumable deployment step state in Postgres** — identical to the SaaS path: per-step state (`Pending → Running → Succeeded | Failed`) in KrakenDeploy's own schema, **idempotent** steps, a claim-with-lease (`SELECT … FOR UPDATE SKIP LOCKED` + heartbeat), and **resume-from-last-step** on restart — never two executors on one release. This is what lets you drain and restart an executor without abandoning a customer deployment.

- **The monolith as ONE versioned unit — not a separate executor tier.** UI + orchestrator + Hangfire ship and version together (D-bg-1). What makes a drain or node loss safe is the durable step ledger above (not a separate tier): a gracefully draining slot finishes its own deployments, and on a hard loss a peer node's *same-release* executor reclaims the lease from its last completed step.
- **The slot + per-node YARP layer** for app-version upgrades ([`blue-green-slot-deployment.md`]) — three fixed slots per node, each a full release, with a per-node YARP routing `kd_ver` to the local slot. Every node runs all three slots, so draining a whole node (§6) drains its slots together without losing version correctness; circuits re-pin to a peer node's instance of the same release.

## 4. Postgres HA & backup is now YOUR job

The single biggest difference from the managed path. On a self-managed/plain-VM install **you** own what DO Managed PostgreSQL would otherwise do:

- **Replication + failover.** A primary with one or more streaming-replication standbys, plus automatic failover — `Patroni` (+ `etcd`/`Consul`) or `repmgr`. The app's connection string points at a leader-following endpoint (`HAProxy`, or `PgBouncer`/`Pgpool`) so a failover needs no app change.
- **Backups + PITR.** `pgBackRest` (or `Barman`) for full/differential/incremental backups plus WAL archiving, shipped to object storage or a Hetzner Storage Box. Test restores on a schedule — an untested backup is a hope, not a backup.
- **Connection pooling.** `PgBouncer` in front of Postgres; KrakenDeploy uses a Scoped `IDbContextFactory` (no `DbContext` pooling), so server-side pooling carries the connection count.

This is the price of the "managed Postgres minimum" not being met by the platform: you are the manager. (Contrast [`saas-multi-account-architecture.md`] §13, where the platform does all of this.)

## 5. Expand/contract migrations

Same discipline as the SaaS path: **expand** (additive only) before rollout → **deploy** code tolerant of both schema shapes → **contract** in a later release. Two releases coexist during a slot overlap (and old/new nodes coexist during a node drain), so a breaking migration shipped in lockstep will fault. Run the migrator yourself against the **primary** (replicas follow via replication):

```bash
# startup-project = the Data project; pin the TFM
dotnet ef database update --project src/KrakenDeploy.Server.Data --framework net10.0
```

No renames/drops and no NOT-NULL-without-default in the expand phase.

## 6. Node-maintenance runbook (drain a whole node)

App-**version** upgrades use the blue-green slot flip (deploy to a spare slot, flip the default — [`blue-green-slot-deployment.md`]), **not** this runbook. Use the sequence below for **node / host maintenance** — OS patch, kernel update, reboot, hardware, or a host-level breaking change — where you must take a whole node, and therefore *all three of its slots*, out of service.

```
0. Pick the node to service; ensure peers can carry its load (HA: >=2 nodes). Verify a fresh backup.
1. Fail the node's /healthz -> Caddy stops routing NEW circuits there; the cookie `fallback`
   re-pins new/reconnecting circuits to peer nodes, where YARP routes the same `kd_ver` to the
   SAME release (every node runs every slot -> worst case is a fresh circuit on the same release,
   never a wrong version).
2. Drain ALL slots on the node together: existing circuits idle out or reconnect to peers
   (.NET 10 state persistence resumes them); let any in-flight deployments running in the node's
   slots FINISH -- or, if you must take the node now, let a peer node's same-release executor
   reclaim them via the heartbeat lease + step ledger. NEVER hard-kill a running deployment.
3. When the node's slots hold zero in-flight deployments, do the maintenance (patch / kernel / reboot).
4. /healthz green -> Caddy resumes routing. Next node. Never drain below the HA minimum (>=1 healthy node).
```

A host-level change that *also* needs a breaking schema change still follows expand/contract (§5): EXPAND before, CONTRACT in a later release. The node drain itself deploys no new release — the same release comes back up on the serviced node.

## 7. Draining circuits with Caddy

Caddy `lb_policy cookie` + active health checks **is** the drain mechanism: failing a node's `/healthz` makes Caddy stop routing new circuits there, the cookie `fallback` re-routes, and `flush_interval -1` keeps WebSocket streaming. Tune `health_interval`/`health_timeout` so an intentionally-drained node is pulled quickly. Existing circuits either complete or reconnect; with circuit state persistence the user resumes mid-task rather than losing work. Because the node's local YARP fronts all three slots, failing the node's `/healthz` drains the node *as a whole* — all three slots at once — which is exactly what node maintenance (§6) needs; it does not require, and does not use, any L4 load balancer.

## 8. Bootstrap caveat

The node driving the rollout cannot drain itself to zero. In a 2-node HA pair, service the standby-role node first, fail over, then service the other. Never drain both app nodes simultaneously, and never restart a node (or retire a slot) while one of its slots holds an in-flight deployment lease.

## 9. References

- KrakenDeploy: [`saas-multi-account-architecture.md`](saas-multi-account-architecture.md) §13 (DO-managed counterpart), [`blue-green-slot-deployment.md`](blue-green-slot-deployment.md) (slot scheme for app-version upgrades), [`ha-pair.md`](ha-pair.md), [`deploy/caddy`](../deploy/caddy).
- Caddy `reverse_proxy` (`lb_policy cookie`, health checks, WebSocket) — https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
- ASP.NET Core Data Protection — key-ring storage providers (`PersistKeysToDbContext`) — https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview
- Blazor Server circuit handling & state persistence (.NET 10) — https://learn.microsoft.com/aspnet/core/blazor/fundamentals/signalr
- EF Core — applying migrations at deployment — https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying
- Patroni (HA templating for PostgreSQL) — https://patroni.readthedocs.io/
- pgBackRest (backup + PITR) — https://pgbackrest.org/
- PostgreSQL streaming replication / warm standby — https://www.postgresql.org/docs/current/warm-standby.html
- keepalived (VRRP virtual IP) — https://www.keepalived.org/

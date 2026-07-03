# High-Availability Pair

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-01 |
| **Authors** | Domagoj Jugović (LAUS CC) — drafted with Claude Code |
| **Status** | `Review` |
| **Technologies** | .NET 10, ASP.NET Core / SignalR, PostgreSQL, Caddy |
| **Projects** | `KrakenDeploy.Server`, `KrakenDeploy.Server.Transport` |

A KrakenDeploy HA pair is two server nodes sharing a single PostgreSQL instance,
behind a **sticky-session Caddy edge that is itself run HA** — a single Caddy node
would be a fleet-wide SPOF (see [Caddy HA](#caddy-ha-the-edge-itself)). No Redis
required.

> **Coordination model (read this first).** There is **no shared agent-connection
> registry**. Each node keeps its live agent connections in an in-memory registry
> (`InMemoryAgentConnectionRegistry`, used in all modes). An earlier design backed
> this with a Postgres `agent_connections` UNLOGGED table; that table was **never
> read** (all reads are node-local) and was **removed** — migration
> `20260630122029_DropAgentConnectionsTable`. HA correctness therefore rests on
> **sticky sessions** pinning each agent to one node, not on shared state. A
> dropped connection self-heals: the agent reconnects and re-registers on whatever
> node it lands on (unlike Hangfire jobs, connection state needs no durability).

## Topology

```
                        ┌───────────────┐
                        │  Floating IP  │  keepalived/VRRP  (or an L4 LB)
                        └───────┬───────┘
                  ┌─────────────┴─────────────┐
            ┌─────┴─────┐               ┌─────┴─────┐
            │  Caddy A  │               │  Caddy B  │  identical config + cookie secret
            └─────┬─────┘               └─────┬─────┘
                  └─────────────┬─────────────┘   (sticky-session load balancing)
                    ┌───────────┴───────────┐
             ┌──────┴──────┐          ┌──────┴──────┐
             │  Server A   │          │  Server B   │
             └──────┬──────┘          └──────┬──────┘
                    └───────────┬───────────┘
                          ┌─────┴─────┐
                          │ Postgres  │
                          └───────────┘
```

## Agent routing

Agents open a long-lived SignalR WebSocket connection. Because the registry is
node-local, the load balancer **must** pin each agent to a single node for the
life of the connection — otherwise a server→agent push (deployment dispatch,
ad-hoc script) issued on the node that does *not* hold the connection cannot find
it. When a node restarts, its agents reconnect and the balancer may route them to
either node; they re-register there.

## Configuration

### Caddy (sticky sessions)

The shipped `deploy/onprem/Caddyfile` single-targets one server. For a pair,
replace the single reverse_proxy with a sticky-balanced pool:

```caddy
handle /hubs/* {
    reverse_proxy server-a:5080 server-b:5080 {
        flush_interval -1
        lb_policy cookie kd_node <shared-secret>   # sticky; fixed secret → stable map
        header_up X-Forwarded-Proto {scheme}
    }
}
```

A fixed `<shared-secret>` keeps the cookie→node map stable across Caddy restarts,
and — critically for the HA edge below — makes **both** Caddy nodes map the same
agent to the same server node.

### Caddy HA (the edge itself)

A single Caddy node is a fleet-wide single point of failure. Run **two** Caddy
nodes and put a virtual IP in front of them — either:

- a **floating/virtual IP via `keepalived` (VRRP)**, shared by the two nodes and
  moved to the survivor on failure (simplest for a self-managed VM pair); or
- a **small L4 (TCP) load balancer** in front of both (a cloud L4 LB, or HAProxy
  in TCP mode).

Give both Caddy nodes **identical config and the same `lb_policy cookie` secret**
so they map each sticky cookie to the same server node; failover is then seamless
(a client that moves from Caddy A to Caddy B still lands on the node holding its
connection). Same edge-HA guidance as the multi-node SaaS path — see
[`self-upgrade-ha.md`](self-upgrade-ha.md) ("HA for Caddy itself"),
[`blue-green-slot-deployment.md`](blue-green-slot-deployment.md) (D-bg-6), and the
header of [`deploy/saas/Caddyfile`](../deploy/saas/Caddyfile).

> **TLS caveat (on-prem HTTP-01).** On-prem issues certs via ACME **HTTP-01**, but
> only the Caddy node currently holding the floating IP can answer the `:80`
> challenge — so the two nodes must **share Caddy's certificate/ACME storage**
> (a mounted share, or Caddy's `storage` module pointed at shared/remote storage);
> otherwise the standby cannot obtain or renew certs and fails closed on takeover.
> An L4 LB that forwards `:80` to a live node, or switching to DNS-01 issuance,
> sidesteps this.

### Server nodes

No special mode is needed — both nodes run the same binary and share the same
`ConnectionStrings__KrakenDb` pointing at the single Postgres instance. Sticky
routing does the coordination.

> **`Server__HaMode` is currently inert.** The env var still appears in the
> on-prem compose/`.env.example`/README as a placeholder, but application code
> ignores it (there is no Postgres-backed registry to select). Do not rely on it.

### Shared state across nodes

- **Encryption master key + license** must match on both nodes (same
  `Encryption__MasterKey` / license env vars) or encrypted variables and license
  checks diverge.
- **Data Protection key ring** must be **shared** so web-UI auth cookies and
  antiforgery tokens issued by one node validate on the other. Point
  `DataProtection__KeyPath` at a shared volume on both nodes. Note: on Windows the
  ring is additionally wrapped with per-host DPAPI, which breaks a shared ring —
  use a Linux node pair (or a non-DPAPI protector) for a Windows HA deployment.
- **Packages / artifacts (`data/`)** are node-local unless placed on a shared
  volume. A deployment can only stream packages that live on the node processing
  it, so put `data/` on shared storage if either node may process any deployment.

## Failure modes

- **Node restart** — that node's in-memory registry is empty on boot; its agents
  reconnect and register fresh (on either node, per sticky routing).
- **Caddy node failure** — the floating IP (keepalived/VRRP) or L4 LB shifts
  traffic to the surviving Caddy node. Identical config + cookie secret means
  clients keep mapping to the same server node, so failover does not trigger an
  agent reconnection storm.
- **Postgres failure** — both nodes lose their DB; agents retry and reconnect once
  Postgres recovers. No agent state is lost that isn't rebuilt on reconnect.
- **Misrouted SignalR message** — if sticky sessions are misconfigured, a push can
  reach the node that does not hold the connection and silently no-op. Correct
  stickiness is the load-bearing requirement here.

## Limitations

- **Sticky sessions are mandatory**, not an optimization — the node-local registry
  has no cross-node lookup.
- **Scale-out beyond a sticky pair** (≥3 nodes, or non-sticky routing) needs a real
  cross-node registry via a **SignalR backplane (e.g. Redis)**. That is deferred
  until a backplane is introduced (M10.2 cloud hardening).

# High-Availability Pair

A KrakenDeploy HA pair consists of two server nodes sharing a single PostgreSQL
instance. No Redis required — the agent connection registry uses a Postgres
UNLOGGED table for live connection state.

## Topology

```
                            ┌──────────┐
                            │  Caddy   │  (sticky-session load balancer)
                            └────┬─────┘
                     ┌───────────┴───────────┐
              ┌──────┴──────┐          ┌──────┴──────┐
              │  Server A   │          │  Server B   │
              └──────┬──────┘          └──────┬──────┘
                     └───────────┬───────────┘
                            ┌────┴─────┐
                            │ Postgres │
                            └──────────┘
```

## Agent Routing

Agents open a long-lived SignalR WebSocket connection. The sticky-session layer
(Caddy with `lb_policy by_cookie`) must pin each agent to a specific node.
When a node restarts, its agents reconnect — the load balancer may route them to
either node.

## Configuration

### Caddy (sticky sessions)

In your Caddyfile, replace the single reverse_proxy with a load-balanced pool:

```caddy
handle /hubs/* {
    reverse_proxy server-a:5080 server-b:5080 {
        flush_interval -1
        lb_policy uri_hash    # or header X-Agent-Id
        header_up X-Forwarded-Proto {scheme}
    }
}
```

### Server nodes

Set `Server__HaMode=Postgres` on both nodes. They share the same
`ConnectionStrings__KrakenDb` pointing to the single Postgres instance.

### No shared filesystem needed

The agent connection table is the only coordination point. Each node maintains
its own `data/` volume for packages, artifacts, and logs. License and encryption
keys must match across nodes (use the same env vars).

## Failure Modes

- **Node restart**: The connection table is truncated on startup. All agents
  reconnect and register fresh.
- **Postgres failure**: Both nodes lose agent tracking. Agents will retry and
  reconnect when Postgres recovers.
- **Split brain**: Not possible — there is no leader election. The connection
  table resolves conflicts via `ON CONFLICT ... DO UPDATE`.

## Limitations

- 2-node maximum for the Postgres-backed registry. Beyond 2 nodes, switch to
  Redis (M10.2 cloud hardening).
- Sticky sessions must be configured correctly — a misrouted SignalR message
  reaches the wrong node and the agent connection won't be found.

# KrakenDeploy — Caddy Reference Deployment

Production-ready Docker Compose stack: **PostgreSQL** → **KrakenDeploy Server** → **Caddy** reverse proxy with automatic HTTPS.

## Prerequisites

- **Docker Engine** 24+ and **Docker Compose** v2+
- A **public DNS A/AAAA record** pointing to the host (for Let's Encrypt)
- **Ports 80 and 443** open in the firewall (HTTP-01 challenge + HTTPS)
- 2 GB RAM, 10 GB disk (minimum)

## Quick start

```bash
cd deploy/caddy

# Set required secrets
export DOMAIN=kraken.example.com
export POSTGRES_PASSWORD=$(openssl rand -base64 32)
export ENCRYPTION_KEY=$(openssl rand -base64 32)
export AGENT_JWT_KEY=$(openssl rand -base64 32)

# Start the stack
docker compose up -d

# Create the first admin user
docker compose exec kraken-server \
  dotnet KrakenDeploy.Server.dll users create-admin \
  --email admin@example.com \
  --password <your-password>

# Login at https://kraken.example.com
```

## Architecture

```
 Internet
    │
    ▼
┌─────────┐      ┌──────────────────┐      ┌──────────┐
│  Caddy  │─────▶│  KrakenDeploy    │─────▶│ Postgres │
│ :80/443 │      │  Server :5080    │      │ :5432    │
│  HTTPS  │      │  (HTTP internal) │      │          │
└─────────┘      └──────────────────┘      └──────────┘
```

- **Caddy** terminates TLS, auto-renews certificates via Let's Encrypt, and proxies HTTP/2 and WebSocket traffic.
- **KrakenDeploy Server** handles Blazor UI, SignalR hubs (`/hubs/agent`, `/hubs/ui`), gRPC services, and the REST API.
- **PostgreSQL 16** stores all data. The volume `pg-data` survives container restarts.

## Environment variables

| Variable | Required | Purpose |
|---|---|---|
| `DOMAIN` | yes | Public hostname (e.g. `kraken.example.com`) |
| `POSTGRES_PASSWORD` | yes | Postgres superuser password |
| `ENCRYPTION_KEY` | yes | Base64-encoded 32-byte AES-256-GCM master key |
| `AGENT_JWT_KEY` | yes | At least 32 chars for HS256 agent JWT signing |
| `API_KEY` | no | CLI API key; omit to disable API-key auth |

## Persistent data

| Volume | Path | Contents |
|---|---|---|
| `kraken-pg-data` | `/var/lib/postgresql/data` | Postgres database |
| `kraken-data` | `/data` | Packages, artifacts, logs, agent binaries |
| `kraken-caddy-data` | `/data` | TLS certificates and OCSP staples |
| `kraken-caddy-config` | `/config` | Caddy auto-saved config |

## TLS / certificates

Caddy obtains certificates from **Let's Encrypt** automatically on first start. Requirements:

1. The `DOMAIN` must resolve to the host's public IP.
2. Port **80** must be reachable from the internet for the HTTP-01 challenge.
3. Once the certificate is issued, port 80 can be closed (Caddy will renew over HTTPS on port 443).

Certificates renew automatically 30 days before expiry. Verify with:

```bash
docker compose logs caddy | grep "certificate renewed"
```

## SignalR and gRPC tuning

Long-lived connections (SignalR, Blazor Server circuits, gRPC streams) require relaxed proxy timeouts. The `Caddyfile` configures:

- **SignalR hubs** (`/hubs/*`): WebSocket upgrade, `flush_interval -1` for real-time message delivery.
- **Blazor circuits** (`/_blazor*`): Same WebSocket semantics.
- **gRPC** (`/kraken.*`): Proxied to `h2c://` (HTTP/2 cleartext) so bidirectional streaming works end-to-end.

No additional Caddy configuration is needed — these are applied automatically by the `handle` directives in the `Caddyfile`.

## Logging

Caddy access logs are written to `/var/log/caddy/kraken.log` inside the container in JSON format. Logs rotate daily, keeping 10 files (30 days).

```bash
# Tail access logs
docker compose exec caddy cat /var/log/caddy/kraken.log

# Server logs
docker compose logs kraken-server
```

## Backup & restore

```bash
# Backup Postgres
docker compose exec postgres pg_dump -U kraken krakendeploy > backup.sql

# Backup server data (packages, artifacts)
docker compose cp kraken-server:/data ./backup-data/

# Restore Postgres
docker compose exec -T postgres psql -U kraken krakendeploy < backup.sql

# Restore server data
docker compose cp ./backup-data/. kraken-server:/data/
```

## Upgrading

```bash
# Pull new images
docker compose pull

# Apply database migrations and restart
docker compose up -d
```

The server auto-applies EF Core migrations on startup in Production mode. No manual migration step is required.

## Troubleshooting

**Caddy fails to obtain a certificate:**
Check that `DOMAIN` resolves to the host's IP and port 80 is open. View logs: `docker compose logs caddy`.

**Server can't connect to Postgres:**
Verify the `POSTGRES_PASSWORD` matches and the `postgres` service is healthy: `docker compose ps`.

**Admin user already exists:**
The `users create-admin` command is idempotent — it exits 0 with "already exists" if the email is taken. Use a different email for additional admins.

**Migration failures on restart:**
EF Core migrations run automatically. If a migration fails, check server logs (`docker compose logs kraken-server`). Manual fix: `docker compose exec kraken-server dotnet ef database update`.

# KrakenDeploy — On-Prem Deployment

Single-command production deployment with Postgres, Caddy reverse-proxy (auto-HTTPS), and the KrakenDeploy server.

## Quick Start

```bash
# 1. Configure
cp .env.example .env
# Edit .env — fill in POSTGRES_PASSWORD, AGENT_JWT_KEY, ENCRYPTION_KEY, and DOMAIN

# 2. Build the server image (or pull from your registry)
docker build -t krakendeploy-server:latest -f ../../Dockerfile.server ../..

# 3. Start
docker compose up -d

# 4. Create admin user
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll users create-admin --email admin@example.com --password <your-password>

# 5. Login at https://<your-domain>/
```

## Architecture

```
Internet → :80/:443 → Caddy → kraken-server:5080 → Postgres:5432
```

- **Caddy**: auto-HTTPS via Let's Encrypt, WebSocket upgrade for SignalR/Blazor, HTTP/2 for gRPC
- **Server**: ASP.NET Core 9 Blazor InteractiveServer, Hangfire background jobs
- **Postgres**: PostgreSQL 16 Alpine, persistent named volume

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `POSTGRES_PASSWORD` | Yes | Postgres superuser password |
| `AGENT_JWT_KEY` | Yes | Minimum 32 chars — agent authentication signing key |
| `ENCRYPTION_KEY` | Yes | Base64 32 bytes — AES-256-GCM master key for sensitive variables |
| `KRAKEN_LICENSE_KEY` | No | License key (can also be uploaded via UI) |
| `DOMAIN` | No | Public domain for auto-HTTPS (defaults to localhost) |
| `HA_MODE` | No | Set to "Postgres" for a 2-node HA pair |
| `SERVER_IMAGE` | No | Docker image tag (default: krakendeploy-server:latest) |

## Backup

```bash
# Full backup (database + data directory)
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll backup --to /data/backups

# Copy backup off-host
docker compose cp kraken-server:/data/backups/kraken-backup-<timestamp> ./backup/
```

## Restore

```bash
# Copy backup to server container
docker compose cp ./backup/kraken-backup-<timestamp> kraken-server:/data/restore/

# Restore
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll restore --from /data/restore/kraken-backup-<timestamp>
```

## Upgrade

```bash
# 1. Pull or build the new server image
docker pull your-registry/krakendeploy-server:latest

# 2. Recreate containers (database migrations run automatically via kraken-init)
docker compose up -d
```

## Rollback

```bash
# 1. Stop the server
docker compose stop kraken-server

# 2. Restore database from backup
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll restore --from /data/restore/kraken-backup-<timestamp>

# 3. Switch image tag and restart
export SERVER_IMAGE=krakendeploy-server:previous
docker compose up -d
```

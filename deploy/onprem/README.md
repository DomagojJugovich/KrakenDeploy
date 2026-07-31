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
| `ENCRYPTION_KEY` | Yes | Base64 32 bytes — the master key (KEK) that wraps the DB-resident data-encryption key. See the warning below. |
| `KRAKEN_LICENSE_KEY` | No | License key (can also be uploaded via UI) |
| `DP_CERT_PATH` | Yes | Host path to the DataProtection PFX (Linux key-ring encryption). See below. |
| `DP_CERT_PASSWORD` | No | Password for the PFX (recommended) |
| `STEP_SIGNING_PUBKEY_PATH` | Yes | Host path to the step-signing **public** key PEM. Servers verify every step-package archive (uploads, catalog installs, the boot seeder) against it. See below. |
| `DOMAIN` | No | Public domain for auto-HTTPS (defaults to localhost) |
| `HA_MODE` | No | Set to "Postgres" for a 2-node HA pair |
| `SERVER_IMAGE` | No | Docker image tag (default: krakendeploy-server:latest) |

### DataProtection certificate (required on Linux)

The server runs in a Linux container, which has no Windows DPAPI, so the
ASP.NET DataProtection key ring — the keys that sign and encrypt auth and
antiforgery cookies — is encrypted at rest with an X.509 certificate. Without
one the ring would be plaintext on disk and anyone who could read the key
directory could forge login cookies, so the server **refuses to boot in
production without it**.

Provide your own PFX (from your PKI or a self-signed one) and keep it **outside
the `kraken-data` volume** — co-locating the key with the ring it protects
defeats the purpose. Generate a self-signed one valid for 10 years:

```bash
mkdir -p secrets
openssl req -x509 -newkey rsa:2048 -keyout secrets/dp-key.pem \
  -out secrets/dp-crt.pem -days 3650 -nodes -subj "/CN=krakendeploy-dataprotection"
openssl pkcs12 -export -out secrets/dp-cert.pfx \
  -inkey secrets/dp-key.pem -in secrets/dp-crt.pem -passout pass:CHANGE_ME
rm secrets/dp-key.pem secrets/dp-crt.pem   # the PFX is self-contained
chmod 644 secrets/dp-cert.pfx              # readable by the container's non-root user
```

The PFX must be **world-readable** (`chmod 644`): it is mounted read-only and
read by the container's non-root `kraken` user, and the password (not the file
permissions) is what protects it.

Then in `.env`: `DP_CERT_PATH=./secrets/dp-cert.pfx` and
`DP_CERT_PASSWORD=CHANGE_ME`. Back the PFX up independently — losing it logs
every user out (a new ring is generated), but it is not catastrophic like
`ENCRYPTION_KEY`. In an **HA pair**, every node must mount the **same** PFX so
they can read each other's cookies. On a **Windows** host DPAPI is used
automatically and no cert is needed.

### Step-package signing (required)

Every step package — the executable plugins deployment steps run — is a
signed archive. A production server (`AllowUnsignedUploads` is `false` outside
Development) refuses to install or even **seed its own built-ins** unless the
archive's RSA-SHA256 signature verifies against the configured trusted key,
and agents apply the same check before loading a package
(`StepPackages:AllowUnsignedLoads` is a Development-only escape hatch).

One key pair per installation owner. Generate it once:

```bash
openssl genrsa -out secrets/kraken-signing.pem 3072
openssl rsa -in secrets/kraken-signing.pem -pubout -out secrets/kraken-signing.pub.pem
chmod 600 secrets/kraken-signing.pem
chmod 644 secrets/kraken-signing.pub.pem
```

- The **private** key signs archives at image build. Build the production
  image with it (BuildKit secret — the key never enters an image layer):

  ```bash
  docker build -f Dockerfile.server \
    --secret id=kraken_signing_key,src=./secrets/kraken-signing.pem \
    -t krakendeploy-server:latest .
  ```

  The same key belongs in the `KRAKEN_SIGNING_KEY` GitHub Actions secret so
  the `publish-step-packages` workflow signs catalog releases with it.
- The **public** key is what this compose file mounts: in `.env` set
  `STEP_SIGNING_PUBKEY_PATH=./secrets/kraken-signing.pub.pem`. Agents
  installed from this server need the same value configured as
  `StepPackages:TrustedPublicKey` (inline PEM or a path) to load the
  packages they download.

Losing the private key is not catastrophic — generate a new pair, rebuild
the image, redistribute the public key — but every previously signed archive
then fails verification, so treat it like any other release-signing key.

> A future guided on-prem installer is planned to offer generating this
> certificate (and `ENCRYPTION_KEY`) during setup; until then it is a manual
> prerequisite.

> **⚠ ENCRYPTION_KEY is not in the backup — preserve it separately.**
> `ENCRYPTION_KEY` is an **env-only KEK**. Every sensitive value in the database
> (variables, agent bundle/HMAC keys, integration secrets) is encrypted under a
> data-encryption key that is itself wrapped by this KEK. The backup bundle
> contains the database dump and the data directory, but **deliberately never
> the KEK** — a leaked dump must not also carry the key that decrypts it. That
> means a database dump is **undecryptable without the exact same
> `ENCRYPTION_KEY`**. Store it in a secrets manager / offline vault, back it up
> independently, and rotate it only via the documented `rotate-kek` flow.
> Losing it is unrecoverable: the encrypted data cannot be read back.
>
> It must be identical across `kraken-init` and `kraken-server` (compose wires
> both from the one `.env` value); `kraken-init` fails fast at
> `database setup` if it is missing, rather than silently provisioning an
> unrecoverable key.

## Backup

The data directory lives at `/var/lib/krakendeploy` inside the container (the
persisted `kraken-data` volume). The KEK (`ENCRYPTION_KEY`) is **not** included —
see the warning above.

```bash
# Full backup (database + data directory)
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll backup --to /var/lib/krakendeploy/backups

# Copy backup off-host
docker compose cp kraken-server:/var/lib/krakendeploy/backups/kraken-backup-<timestamp> ./backup/
```

## Restore

Restore requires the original `ENCRYPTION_KEY` in `.env` — the dump is
undecryptable without it.

```bash
# Copy backup to server container
docker compose cp ./backup/kraken-backup-<timestamp> kraken-server:/var/lib/krakendeploy/restore/

# Restore
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll restore --from /var/lib/krakendeploy/restore/kraken-backup-<timestamp>
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
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll restore --from /var/lib/krakendeploy/restore/kraken-backup-<timestamp>

# 3. Switch image tag and restart
export SERVER_IMAGE=krakendeploy-server:previous
docker compose up -d
```

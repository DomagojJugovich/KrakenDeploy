# KrakenDeploy — On-Premises Deployment Guide

## System Requirements

| Resource | Minimum | Recommended |
|----------|---------|-------------|
| OS | Windows Server 2019+, Ubuntu 22.04+, Debian 12+, RHEL 9+ | Same |
| RAM | 2 GB | 4 GB |
| Disk | 10 GB + package storage | 50 GB SSD |
| Database | PostgreSQL 16 | PostgreSQL 16 with SSD storage |
| .NET | .NET 9 Runtime (bundled with Docker/installer) | Same |
| Network | Outbound HTTPS (443) for Let's Encrypt; inbound 80/443 | Static IP recommended |

## Installation Paths

### Path A: Docker Compose (recommended)

**Simplest path — one command to bring everything up.**

```bash
# 1. Clone or download the deploy/onprem directory
# 2. Configure environment
cp .env.example .env
# Fill in POSTGRES_PASSWORD, AGENT_JWT_KEY, ENCRYPTION_KEY, DOMAIN

# 3. Build server image
docker build -t krakendeploy-server:latest -f ../../Dockerfile.server ../..

# 4. Start
docker compose up -d

# 5. Create admin user
docker compose exec kraken-server dotnet KrakenDeploy.Server.dll users create-admin \
    --email admin@example.com --password <your-password>

# 6. Login at https://<your-domain>/
```

See `deploy/onprem/README.md` for backup, restore, upgrade, and rollback procedures.

### Path B: Manual (Windows or Linux)

**For users who prefer to run the server directly (no Docker).**

```powershell
# 1. Install .NET 9 SDK/Runtime
# 2. Install PostgreSQL 16

# 3. Create the database
dotnet KrakenDeploy.Server.dll database create \
    --host localhost --username postgres --password <pwd> --database-name krakendeploy

# 4. Run setup (migrations + seed)
dotnet KrakenDeploy.Server.dll database setup \
    --connection-string "Host=localhost;Port=5432;Database=krakendeploy;Username=postgres;Password=<pwd>"

# 5. Create admin user
dotnet KrakenDeploy.Server.dll users create-admin \
    --email admin@example.com --password <your-password>

# 6. Start the server
dotnet KrakenDeploy.Server.dll
```

Configure secrets via environment variables or `appsettings.Production.json`:

| Variable | Purpose |
|----------|---------|
| `ConnectionStrings__KrakenDb` | Postgres connection string |
| `Agent__JwtSigningKey` | Minimum 32 chars — agent auth signing key |
| `Encryption__MasterKey` | Base64 32 bytes — AES-256-GCM master key |
| `KRAKEN_LICENSE_KEY` | License key (or upload via UI) |

### Path C: Windows MSI / Linux package

Installers are in development (M10.1 slices 7-8). For now use Docker Compose
or manual deployment.

## License Activation

1. Login as the admin user created during setup.
2. Go to **Settings** → **License**.
3. Paste your license key and click **Validate & Activate**.
4. The license is validated locally — no internet connection required.

Alternatively, set the `KRAKEN_LICENSE_KEY` environment variable before starting the server.

## Configuring Authentication

KrakenDeploy supports local accounts (email/password) and OIDC single sign-on.

### Local accounts (default)

The bootstrap admin is created via the CLI. Additional users are invited through
**Configuration** → **Users** → **Invite User**.

### API keys for CLI / REST access

The legacy shared `ApiKey:Key` configuration value was **removed**. Requests
carrying the old static key are rejected with `401`. API access now uses
**per-user keys** (hashed at rest, revocable, optionally Space-restricted):

1. **UI:** Configuration → **API Keys** → create a key (shown once — copy it).
2. **CLI (headless bootstrap):**
   `dotnet KrakenDeploy.Server.dll apikeys create --user <email> --name <purpose>`

Pass the key in the `X-Api-Key` header. If a leftover `ApiKey:Key` value is
still present in your configuration, the server logs a one-time warning at
startup and ignores the value — remove it and mint a per-user key instead.

### OIDC (recommended for production)

1. Set up an OIDC application in your identity provider. See `docs/oidc-templates/`
   for step-by-step guides for Entra ID, Okta, Google Workspace, ADFS, and Azure AD.
2. Go to **Configuration** → **Identity Providers** → **New Identity Provider**.
3. Enter the Authority, Client ID, Client Secret, and other fields from the guide.
4. Users will see a "Sign in with {Provider}" button on the login page.
5. First-time OIDC sign-in automatically creates the user account.

## Backup and Restore

### Backup

```bash
dotnet KrakenDeploy.Server.dll backup --to /path/to/backups
```

This creates a timestamped directory containing:
- `database.sql` — full PostgreSQL dump
- `data/` — server data directory (packages, artifacts, agent binaries)
- `manifest.json` — server version and metadata

### Restore

```bash
dotnet KrakenDeploy.Server.dll restore --from /path/to/backups/kraken-backup-<timestamp>
```

**Important:** The server must be stopped during restore. Downgrade the server
binary to the backup's version if restoring an older backup.

### Automated backups (recommended)

On Linux:
```bash
# /etc/cron.d/kraken-backup
0 3 * * * kraken dotnet /opt/krakendeploy/KrakenDeploy.Server.dll backup --to /opt/krakendeploy/backups
```

On Windows, use Task Scheduler to run the backup command nightly.

## Upgrade Procedure

1. **Stop the server** (stop the service or `docker compose stop`).
2. **Install the new version** (new MSI, new Docker image, or new binaries).
3. **Apply migrations** — run `database setup` (idempotent; applies pending
   migrations and re-seeds built-ins). Automatic startup migration runs **only**
   in the `Development` environment, so a production upgrade requires this
   explicit step.
4. **Start the server.**

## Rollback Procedure

1. **Restore the database** from the most recent backup.
2. **Restore the data directory** from the same backup.
3. **Downgrade the server binary** to the version that created the backup.
4. **Start the server.**

## High Availability

For larger customers, two server nodes can share a single PostgreSQL instance.
See `docs/ha-pair.md` for the full configuration guide.

## Observability

KrakenDeploy exports OpenTelemetry traces and metrics over OTLP, and can
forward structured logs to a Seq instance. All export is **disabled by
default** — an unconfigured server collects and drops telemetry with zero
startup or runtime cost, exactly as before.

> **Regulated-environment warning:** Enabling export sends operational data
> (request URLs, durations, error messages, machine names) off the host to
> whatever collector you configure. In GDPR / state-institution deployments,
> confirm that your collector and its storage sit inside your data boundary
> before setting `Otel:Enabled` to `true`.

### What is exported

| Signal | Source | Notes |
|--------|--------|-------|
| Traces | ASP.NET Core + HttpClient auto-instrumentation | One span per inbound request and outbound HTTP call |
| Metrics | ASP.NET Core + HttpClient auto-instrumentation | Request counts, durations, active-request gauges |
| Logs | Serilog pipeline | Console + rolling file always; Seq sink when configured |

There is no custom domain instrumentation (no `ActivitySource`, no `Meter`).
Resource attributes: `service.name`, `service.version`, `service.instance.id`
(machine name), and — only on a slotted blue-green release — `kraken.release.id`
and `kraken.release.slot` (stamped per instance via `Release:Id` /
`Release:SlotNo` at deploy time; see `docs/blue-green-slot-deployment.md`).

### Configuration

All keys live under the `Otel` section (`appsettings.Production.json` or
environment variables with `__` separators):

| Key | Default | Purpose |
|-----|---------|---------|
| `Otel:Enabled` | `false` | Master switch — `false` is a true no-op (no OTLP, no Seq) |
| `Otel:OtlpEndpoint` | `""` | Collector OTLP endpoint, e.g. `http://otel-collector:4317` |
| `Otel:Protocol` | `grpc` | `grpc` or `http/protobuf` (any other value fails startup) |
| `Otel:Headers` | `""` | Optional OTLP auth headers, comma-separated `k=v` pairs |
| `Otel:SeqServerUrl` | `""` | Seq ingest URL, e.g. `http://seq:5341` — enables the log sink (requires `Enabled`) |

Environment-variable equivalents (no `appsettings` edit needed):

```bash
Otel__Enabled=true
Otel__OtlpEndpoint=http://otel-collector:4317
Otel__SeqServerUrl=http://seq:5341
```

### Logs: the Seq decision

Logs stay on the Serilog pipeline — they are **not** routed through OTel.
The export leg is `Serilog.Sinks.Seq`, which posts structured events directly
to Seq's native ingest API. This was chosen over an OTLP-logs exporter because
Seq (2024.1+) ingests Serilog events natively with full property fidelity,
whereas its OTLP log ingest is comparatively recent and loses some Serilog
structure. A local Seq container (`datalust/seq`) is the intended smoke-test
target; point `Otel:SeqServerUrl` at it and logs appear immediately. The Seq
sink is gated by the same `Otel:Enabled` master switch as OTLP, so with
`Enabled=false` no telemetry of any kind leaves the host.

### Example: local collector + Seq (Docker Compose)

Add to your compose file alongside the KrakenDeploy server:

```yaml
services:
  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: ["--config=/etc/otelcol/config.yaml"]
    volumes:
      - ./otel-collector.yaml:/etc/otelcol/config.yaml
    ports:
      - "4317:4317"   # OTLP gRPC
      - "4318:4318"   # OTLP HTTP/protobuf

  seq:
    image: datalust/seq:latest
    environment:
      ACCEPT_EULA: "Y"
    ports:
      - "5341:80"     # Seq UI + ingest
```

Minimal `otel-collector.yaml`:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

exporters:
  debug:
    verbosity: detailed

service:
  pipelines:
    traces:
      receivers: [otlp]
      exporters: [debug]
    metrics:
      receivers: [otlp]
      exporters: [debug]
```

Then set `Otel__Enabled=true`, `Otel__OtlpEndpoint=http://otel-collector:4317`,
and `Otel__SeqServerUrl=http://seq:80` on the KrakenDeploy server container.
Spans and metrics print to the collector's stdout; logs appear in the Seq UI
at `http://localhost:5341`.

### Global log search

In-app log viewing in KrakenDeploy is **per-task by design** — each deployment
task shows its own execution log. There is no built-in cross-deployment log
search. Operators who need to search logs across all deployments, all spaces,
or all nodes should point the OTLP/Seq pipeline at their collector and query
Seq (or whatever backend sits behind the collector) directly.

## Troubleshooting

### Database connection refused
- Verify PostgreSQL is running: `pg_isready`
- Check firewall: port 5432 must be reachable from the server

### License not accepted
- Ensure the license key is copied exactly (no extra whitespace)
- Check the server logs for details: `logs/server-*.log`

### OIDC sign-in fails
- Verify the redirect URI is exactly `https://<your-domain>/signin-oidc`
- Check that the client secret hasn't expired
- Look for error messages in the server logs

### Agents can't connect
- Verify `Agent__JwtSigningKey` is at least 32 characters
- Check agent logs for the registration token exchange error
- Ensure the server's public URL is reachable from the agent machine

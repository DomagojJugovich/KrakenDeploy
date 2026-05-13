# KrakenDeploy

A self-hosted, .NET-native deployment platform for Windows and Linux targets. Inspired by Octopus Deploy: projects, environments, releases, deployments, channels, lifecycles, tenants with tag-based scoping, and a pluggable step engine compatible with Octopus step templates.

> **Status:** early development — M1 walking skeleton in progress. Not yet usable in production.
> See [docs/architecture.md](docs/architecture.md) for the system shape and [TASKS.md](TASKS.md) for the milestone plan.

## Highlights

- **Reverse-tunnel agents** — agents dial out over HTTPS; no inbound firewall rules required at customer sites. Direct and polling modes are pluggable behind `IAgentTransport` / `IServerLink`.
- **SignalR + gRPC split transport** — SignalR for control (heartbeats, commands, status, logs); gRPC bidirectional streaming for binary data (package transfer with backpressure and resume).
- **Octopus step-template compatibility** — community step templates from the [Octopus Library](https://github.com/OctopusDeploy/Library) import and run unchanged.
- **Octodiff delta packages** — only ship the bytes that changed.
- **Octostache variable substitution** — same `#{...}` syntax users already know.
- **`StringArray` variables** — multi-value variables with `#{each}`, `#{Var[i]}`, and `| join` filters.
- **Offline Drop with result return** — air-gapped customers get a self-contained drop bundle and return a signed result bundle (manual file, email, HTTP webhook, or SFTP).
- **Cross-platform scripts** — PowerShell 7+ and Bash on every agent, with a `Kraken` helper module that papers over Windows/Linux differences (services, scheduled tasks, firewall, logging, artifacts).

## Stack

| Layer | Choice |
| --- | --- |
| UI | Blazor Web App, InteractiveServer render mode |
| Components | Radzen Blazor |
| Data | EF Core + Npgsql + PostgreSQL (`jsonb`-heavy) |
| Background jobs | Hangfire on Postgres |
| Search | Postgres `tsvector` + `pg_trgm` |
| Agent control | SignalR over outbound HTTPS |
| Agent data | gRPC bidi streaming over outbound HTTPS |
| Delta packages | [Octodiff](https://github.com/OctopusDeploy/Octodiff) |
| Variable substitution | [Octostache](https://github.com/OctopusDeploy/Octostache) |
| Logging | Serilog + OpenTelemetry |

## Repository layout

```
src/
  KrakenDeploy.Server/             Blazor Server UI + API host
  KrakenDeploy.Server.Core/        Domain (no infra refs)
  KrakenDeploy.Server.Data/        EF Core + migrations
  KrakenDeploy.Server.Transport/   SignalR hubs, gRPC services
  KrakenDeploy.Agent/              Cross-platform agent worker service
  KrakenDeploy.Agent.Transport/    IServerLink implementations
  KrakenDeploy.Contracts/          Shared DTOs, hub interfaces, .proto
scripts/
  build.ps1                        Build the solution
  migrate.ps1                      Apply EF Core migrations
  reset-db.ps1                     Drop + recreate dev database
  create-admin.ps1                 Bootstrap the first admin user
  run-server.ps1                   Start the server (dev)
  run-agent.ps1                    Start an agent (dev)
tests/
  KrakenDeploy.Server.Core.Tests/
  KrakenDeploy.Server.Data.Tests/  Uses Testcontainers (Postgres)
  KrakenDeploy.Agent.Tests/
```

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (for the local Postgres container)
- [PowerShell 7+](https://github.com/PowerShell/PowerShell) (`pwsh`) for the helper scripts

## Getting started

### 1 — Start Postgres

```bash
docker compose up -d postgres
```

### 2 — Apply migrations

```bash
dotnet ef database update \
  --project src/KrakenDeploy.Server.Data \
  --startup-project src/KrakenDeploy.Server.Data
```

Or via the script (reads the same local-Postgres defaults):

```pwsh
./scripts/migrate.ps1
```

### 3 — Configure secrets (optional but recommended)

The development `appsettings.Development.json` ships with placeholder values that work against the local Docker Postgres. For a non-default database or a custom JWT signing key, store them in [user-secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) so they never end up in git:

```bash
# Server secrets
dotnet user-secrets set "ConnectionStrings:KrakenDb" \
  "Host=localhost;Port=5432;Database=krakendeploy_dev;Username=postgres;Password=postgres" \
  --project src/KrakenDeploy.Server

dotnet user-secrets set "Agent:JwtSigningKey" \
  "replace-with-a-32-plus-char-random-string" \
  --project src/KrakenDeploy.Server
```

### 4 — Create an admin user

```bash
dotnet run --project src/KrakenDeploy.Server -- \
  users create-admin --email you@example.com --password 'ChangeMe123!'
```

Or:

```pwsh
./scripts/create-admin.ps1 -Email you@example.com -Password 'ChangeMe123!'
```

The command is idempotent: if the user already exists it prints a notice and exits 0.

### 5 — Run the server

```bash
dotnet run --project src/KrakenDeploy.Server
```

Or:

```pwsh
./scripts/run-server.ps1          # HTTPS on https://localhost:5443
./scripts/run-server.ps1 -Profile http  # HTTP on http://localhost:5080
```

Open **https://localhost:5443** in a browser and log in with the admin account created above.

### 6 — Register and run an agent

1. Log into the UI, go to **Infrastructure → Targets**, click **Add Target**.
2. Fill in the target name, roles, and transport mode.
3. Copy the one-time registration token shown on the final step.
4. On the machine that will run the agent:

```bash
dotnet run --project src/KrakenDeploy.Agent -- \
  --Server:Url https://<server-host>:5443 \
  --Server:RegistrationToken <token-from-ui>
```

Or via the script:

```pwsh
./scripts/run-agent.ps1 -RegistrationToken <token-from-ui>
# (ServerUrl defaults to https://localhost:5443 for local dev)
```

On successful registration the token is consumed server-side and the agent writes an identity file to its data directory (`%ProgramData%\KrakenDeploy\Agent` on Windows, `/var/lib/krakendeploy-agent` on Linux/macOS). Subsequent runs need no token.

The **Targets** page in the UI will show the agent as **Online** with hostname, OS, agent version, and a live heartbeat timestamp.

### Health check

```
GET https://localhost:5443/healthz
```

Returns `200 OK` with a JSON body:

```json
{ "status": "ok", "targets": 3, "connectedAgents": 1 }
```

Returns `503` if the database is unreachable.

## Scripts reference

All scripts live in `scripts/` and run with `pwsh` on Windows, Linux, and macOS.

| Script | Purpose |
| --- | --- |
| `build.ps1 [-Configuration Debug\|Release]` | Build the solution |
| `migrate.ps1` | Apply pending EF Core migrations |
| `reset-db.ps1 [-WhatIf]` | Drop + recreate dev database and re-apply migrations |
| `create-admin.ps1 -Email … -Password …` | Create (or no-op if exists) an admin user |
| `run-server.ps1 [-Profile http\|https]` | Start the server in dev mode |
| `run-agent.ps1 [-ServerUrl …] [-RegistrationToken …] [-DataPath …]` | Start an agent |
| `setup-database.ps1 [-ConnectionString …]` | Interactive database creation + migration + seed |
| `backup.ps1 -OutputDirectory <path>` | Full backup (pg_dump + data directory) |
| `restore.ps1 -BackupDirectory <path>` | Restore a backup bundle |

## Deployment

For production deployment, see the [On-Prem Deployment Guide](docs/on-prem-guide.md).

Quick reference:
- **Docker Compose:** `deploy/onprem/` — one-command bring-up with Postgres + Caddy
- **Manual:** `database create` → `database setup` → `users create-admin` CLI flow
- **OIDC templates:** `docs/oidc-templates/` — Entra ID, Okta, Google, ADFS, Azure AD
- **HA pair:** `docs/ha-pair.md` — 2-node setup with shared Postgres

## License

MIT — see [LICENSE](LICENSE).

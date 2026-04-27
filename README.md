# KrakenDeploy

A self-hosted, .NET-native deployment platform for Windows and Linux targets. Inspired by Octopus Deploy: projects, environments, releases, deployments, channels, lifecycles, tenants with tag-based scoping, and a pluggable step engine compatible with Octopus step templates.

> **Status:** early development. Not yet usable. See [TASKS.md](TASKS.md) for the milestone plan and current progress.

## Highlights

- **Reverse-tunnel agents** — agents dial out over HTTPS, no inbound firewall rules required at customer sites. Direct and polling modes are pluggable behind `IAgentTransport` / `IServerLink`.
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
tests/
  ...
```

## Getting started

> Prerequisites: [.NET 9 SDK](https://dotnet.microsoft.com/download), [Docker](https://www.docker.com/) (for Postgres in development).

```bash
# 1. Start Postgres
docker compose up -d postgres

# 2. Apply migrations
dotnet ef database update \
  --project src/KrakenDeploy.Server.Data \
  --startup-project src/KrakenDeploy.Server

# 3. Create an admin user
dotnet run --project src/KrakenDeploy.Server -- \
  users create-admin --email you@example.com --password ChangeMe123!

# 4. Run the server
dotnet run --project src/KrakenDeploy.Server

# 5. Run an agent (on the same or another machine, after generating a registration token in the UI)
dotnet run --project src/KrakenDeploy.Agent -- \
  --Server:Url https://localhost:5443 \
  --Server:RegistrationToken <token-from-ui>
```

## License

MIT — see [LICENSE](LICENSE).

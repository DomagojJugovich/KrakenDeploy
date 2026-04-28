# KrakenDeploy — Task List

A self-hosted, .NET-native deployment platform inspired by Octopus Deploy. This file tracks work milestone-by-milestone.

## Locked decisions

- **License:** MIT
- **UI:** Blazor with InteractiveServer render mode (configurable per-page); Radzen Blazor components
- **Data:** EF Core + Npgsql + PostgreSQL (`jsonb` for variable values, scope filters, snapshots)
- **Background jobs:** Hangfire on Postgres
- **Search:** Postgres `tsvector` + `pg_trgm`
- **Agent transport:** SignalR (control) + gRPC bidi streaming (data) over outbound HTTPS by default; direct and polling modes pluggable behind `IAgentTransport` / `IServerLink`
- **Delta packages:** Octodiff
- **Variable substitution:** Octostache
- **Step-template compatibility:** schema-compatible with `OctopusDeploy/Library/step-templates`
- **Step runtime:** PowerShell 7+ (`pwsh`) and Bash on agents; `Kraken` PowerShell module for cross-platform helpers
- **Topology:** single-server for now, scale-out later (model FKs for Spaces from day one)
- **Agent OS coverage:** Windows + Linux
- **Login UI:** Radzen-styled from the start (no default Identity Razor pages)
- **Admin bootstrap:** CLI command `KrakenDeploy.Server users create-admin` (no env-var seed)

## Required scope items called out explicitly

- **`StringArray` variable type** with `#{each x in Var}`, `#{Var[i]}`, and `#{Var | join "; "}`. Parallel `$OctopusArrays` accessor in PowerShell alongside the existing `$OctopusParameters` for back-compat.
- **Offline Drop targets return a result bundle** for troubleshooting and proof-of-deployment. Bundle = zip with `manifest.json`, `deployment-log.txt`, `deployment-result.json`, `artifacts/`, `machine-info.json`, HMAC `signature.bin`. Delivery channels per target: manual file, auto-email (SMTP config embedded encrypted), HTTP POST webhook, SFTP/file-share drop. Server tracks status as `PendingOfflineResult` until ingested.
- **Kraken.IIS as a comprehensive superset of Octopus.IIS** — full app pool process model, recycle settings, `loadUserProfile`, rapid-fail protection, identity, complete site bindings (cert from variable), application init/preload, URL Rewrite, request filtering, response headers, MIME types, default documents, virtual directories, sub-applications, atomic-swap deploy with rollback, drain-mode recycle, post-deploy health probe. Every input accepts Kraken variable expressions.

---

## M1 — Walking Skeleton

**Exit criterion.** `docker compose up -d postgres`, `dotnet run --project src/KrakenDeploy.Server`, then on any other Windows or Linux box `dotnet run --project src/KrakenDeploy.Agent` against the server URL with a registration token, and the Targets page in the Blazor UI shows the agent as **Online** with hostname, OS, agent version, and a heartbeat timestamp ticking forward. CI runs this end-to-end with the agent in a Linux container against the server in another container.

### Phase 0 — Repo and tooling foundation

- [x] `git init` at `D:\_GITHUB\KrakenDeploy`
- [x] `LICENSE` — MIT, copyright Domagoj Jugović
- [x] `.gitignore` (Visual Studio, Rider, .NET, Node, OS files)
- [x] `.gitattributes` (LF for `.cs`, `.csproj`, `.sh`, `.json`; CRLF for `.bat`, `.ps1`)
- [x] `.editorconfig` (4 spaces, UTF-8, final newline, trim trailing whitespace; C#: nullable, file-scoped namespaces, var preferences)
- [x] `global.json` pinning .NET 9 SDK
- [x] `Directory.Build.props` at repo root: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<LangVersion>latest</LangVersion>`, `<ImplicitUsings>enable</ImplicitUsings>`
- [x] `Directory.Packages.props` for Central Package Management
- [x] `README.md` skeleton (filled in Phase 11)
- [x] First commit: `chore: bootstrap repo`

### Phase 1 — Solution structure

- [x] `KrakenDeploy.sln` at repo root
- [x] Create projects (empty templates):
  - [x] `src/KrakenDeploy.Server` (ASP.NET Core Blazor Web App, InteractiveServer, --empty)
  - [x] `src/KrakenDeploy.Server.Core` (classlib — domain, no infra refs)
  - [x] `src/KrakenDeploy.Server.Data` (classlib — EF Core, migrations)
  - [x] `src/KrakenDeploy.Server.Transport` (classlib — SignalR hubs, gRPC services)
  - [x] `src/KrakenDeploy.Agent` (worker service)
  - [x] `src/KrakenDeploy.Agent.Transport` (classlib — `IServerLink` impls)
  - [x] `src/KrakenDeploy.Contracts` (classlib — shared DTOs, hub interfaces, `.proto`)
  - [x] `tests/KrakenDeploy.Server.Core.Tests`
  - [x] `tests/KrakenDeploy.Server.Data.Tests` (with Testcontainers.PostgreSql)
  - [x] `tests/KrakenDeploy.Agent.Tests`
- [x] Wire project references
- [x] Add CPM packages: Microsoft.AspNetCore.SignalR.Client, Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL, Radzen.Blazor, Serilog.AspNetCore, Serilog.Sinks.File, OpenTelemetry.Extensions.Hosting, xunit, FluentAssertions, Testcontainers.PostgreSql
- [x] `dotnet build` clean

### Phase 2 — Postgres and EF Core

- [x] `docker-compose.yml` at repo root: `postgres:16-alpine`, named volume, healthcheck, port 5432, default db `krakendeploy_dev`
- [x] Domain entities in `Server.Core/Domain/`:
  - [x] `Project` (Id `Guid`, Slug, Name, Description, CreatedUtc, ModifiedUtc)
  - [x] `DeploymentEnvironment` (Id, Slug, Name, SortOrder) — class renamed from `Environment` to avoid clash with `System.Environment`; table is still `environments`
  - [x] `DeploymentTarget` (Id, Name, Status enum, LastSeenUtc, MachineName, OperatingSystem, AgentVersion, Roles `List<string>` → `text[]`, TransportMode enum, RegistrationKeyHash)
  - [x] `Release` (Id, ProjectId, Version, CreatedUtc) — placeholder
  - [x] `Deployment` (Id, ReleaseId, EnvironmentId, TargetId, Status enum, StartedUtc, CompletedUtc) — placeholder
- [x] Base `Entity` class with `Guid.CreateVersion7()` + `IAuditable` interceptor for audit timestamps
- [x] `KrakenDbContext` in `Server.Data` with the DbSets above
- [x] `IEntityTypeConfiguration<>` per entity (snake_case table names via EFCore.NamingConventions, explicit FKs and indexes)
- [x] `jsonb` column convention helper for later (`HasJsonbColumn<T>()` extension)
- [x] `appsettings.Development.json` connection string (user-secrets deferred)
- [x] `DesignTimeDbContextFactory` so `dotnet ef` works (reads `KRAKEN_DESIGN_TIME_CONNECTION_STRING` env var with local-Postgres fallback)
- [x] First migration: `Initial` (timestamp `20260427152352`)
- [x] Auto-apply migrations on startup in Development; document `dotnet ef database update` for Production
- [x] Integration test: spin up Postgres testcontainer, apply migrations, assert `HasPendingModelChanges() == false`, plus CRUD smoke tests (Project audit timestamps, DeploymentTarget role array roundtrip)
- [x] Local tool manifest with `dotnet-ef` 9.0.0 pinned

### Phase 3 — Identity, Radzen-styled login, admin CLI

- [x] Add `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
- [x] `ApplicationUser : IdentityUser<Guid>`; integrate Identity tables into `KrakenDbContext`
- [x] Migration: `AddIdentity`
- [x] Identity config: confirmed account = false, sensible password policy
- [x] **Radzen-styled login page** at `/login` (no default Identity Razor pages):
  - [x] `RadzenCard` centered layout, app logo, email + password fields, "Sign in" button, error display
  - [x] Cookie auth scheme; `[Authorize]` is the default for the rest of the app
  - [x] Logout endpoint
- [x] No registration page (admin-created users only for M1)
- [x] **Admin bootstrap CLI**: `KrakenDeploy.Server users create-admin --email <e> --password <p>`
  - [x] Use `System.CommandLine` or simple arg parsing in `Program.cs`
  - [x] When invoked with `users create-admin`, build host, run command, exit (don't start the web server)
  - [x] Idempotent: if user exists, print "already exists" and exit 0
  - [x] Document in README and print a hint on server startup if zero users exist
- [ ] Defer: roles, fine-grained permissions, OIDC, password reset, MFA

### Phase 4 — Blazor shell with Radzen

- [x] Install `Radzen.Blazor`; register services in `Program.cs`
- [x] `_Imports.razor` adds Radzen usings
- [x] Pick a Radzen theme (recommend `material3` or `software`); reference CSS in `App.razor`
- [x] `MainLayout.razor`:
  - [x] Top bar (`RadzenLayout` + `RadzenHeader`): logo, environment badge from config, user menu (account, logout)
  - [x] Left sidebar (`RadzenSidebar` + `RadzenPanelMenu`):
    - **Deploy:** Projects, Releases, Deployments
    - **Infrastructure:** Targets, Environments, Tenants
    - **Library:** Variable Sets, Step Templates, Lifecycles, Channels
    - **System:** Tasks, Audit, Settings
  - [x] Content area `RadzenBody`
- [x] Empty pages with route, title, `[Authorize]`:
  - [x] `/` Dashboard (placeholder cards: targets online, deployments today, failed deployments, pending offline results)
  - [x] `/projects`, `/projects/{slug}`
  - [x] `/environments`
  - [x] `/targets`
  - [x] `/tenants`
  - [x] `/variable-sets`
  - [x] `/step-templates`
  - [x] `/lifecycles`
  - [x] `/channels`
  - [x] `/releases`
  - [x] `/deployments`
  - [x] `/tasks`
  - [x] `/audit`
  - [x] `/settings`
- [x] `/healthz` minimal-API health endpoint (Postgres ping, target count)

### Phase 5 — CRUD on the M1 entities

- [x] **Projects** page: `RadzenDataGrid` with paging/sorting/filtering, "New Project" via `RadzenDialogService`, edit, delete-with-confirm
- [x] **Environments** page: same pattern, ↑/↓ buttons for `SortOrder` reorder
- [x] **Targets** page: read-only DataGrid (creation via Phase 7 wizard); columns: name, status pill, hostname, OS, agent version, roles, last-seen relative time
- [x] **Releases** / **Deployments** pages: read-only empty grids, populated in M2
- [x] All persistence via service classes in `Server.Data.Services`; no `DbContext` in `.razor` files
- [x] Validation via `DataAnnotations`, surfaced via Radzen form components

### Phase 6 — SignalR agent control hub

- [x] In `Contracts`:
  - [x] `IAgentHubServer` (agent → server): `RegisterAsync`, `HeartbeatAsync`, `ReportStatusAsync`, `AppendLogAsync`
  - [x] `IAgentHubClient` (server → agent): `PingAsync`, `RunDeploymentAsync` (stubbed for M1)
  - [x] `AgentRegistrationRequest` and `HeartbeatRequest` DTOs
- [x] In `Server.Transport`: `AgentHub : Hub<IAgentHubClient>` implementing `IAgentHubServer`; authorize with agent JWT scheme `AgentJwt`
- [x] `IAgentConnectionRegistry` + `InMemoryAgentConnectionRegistry` singleton (ConcurrentDictionary; Redis-backed in scale-out)
- [x] `OnConnectedAsync`: validate agent JWT, mark target Online, update `LastSeenUtc`
- [x] `OnDisconnectedAsync`: fire-and-forget 30 s grace period task; marks Offline if agent hasn't reconnected
- [x] Heartbeat handler: bump `LastSeenUtc`, accept updated machine info (null = no-op)
- [x] `AddSignalR(o => o.MaximumReceiveMessageSize = 1_048_576)` — control plane
- [x] Map hub at `/hubs/agent`; JWT token via `?access_token=` query string for WebSocket compatibility

### Phase 7 — Target registration flow

- [x] "Add Deployment Target" button on `/targets` opens a Radzen dialog wizard:
  - [x] Step 1: name, roles
  - [x] Step 2: transport mode (Reverse default; Direct/Polling available)
  - [x] Step 3: server generates a one-time registration token (24h TTL, single-use, stored hashed); shows install command for Windows and Linux with token embedded
- [x] `POST /api/agents/register` endpoint: exchanges one-time token for a long-lived agent JWT; returns `agentId` + `agentJwt`
- [x] Server stores `RegistrationKeyHash` per target; rotation supported (`TargetRegistrationService.RotateTokenAsync`)
- [x] `AgentJwtService` — HS256 JWT issuance using `Agent:JwtSigningKey`; 1-year token lifetime
- [x] `DeploymentTarget.RegistrationTokenExpiresUtc` column added; EF migration `AddRegistrationTokenExpiry`
- [x] No installer in M1 — instructions tell the user to download binary, pass token flag

### Phase 8 — Agent worker service

- [x] Agent config (env + appsettings + CLI args): `Server:Url`, `Server:RegistrationToken` (one-time), `Agent:DataPath`, `Agent:Roles`
- [x] Persist agent identity to `%ProgramData%\KrakenDeploy\Agent\agent.json` on Windows, `/var/lib/krakendeploy-agent/agent.json` on Linux. File permissions locked to owner on Linux (chmod 600).
- [x] Hosted services in order:
  - [x] `RegistrationHostedService` — loads existing identity or exchanges one-time token (5× retry with exponential back-off); stops host on unrecoverable failure
  - [x] `ServerLinkHostedService` — opens SignalR connection with `WithAutomaticReconnect`, calls `RegisterAsync` with full machine info; reports `ShuttingDown` on exit
  - [x] `HeartbeatHostedService` — every 30s while connected, calls `HeartbeatAsync`
- [x] `AgentContext` singleton — `TaskCompletionSource`-based gate so ServerLink and Heartbeat services wait safely for registration to complete
- [x] `MachineInfoCollector` — hostname, `RuntimeInformation.OSDescription`, agent assembly version, free disk on data-path volume, total RAM via `GC.GetGCMemoryInfo()`
- [x] `AgentIdentityStore` — `System.Text.Json` serialise/deserialise, Unix file mode 600 on Linux/macOS
- [x] Logging: Serilog bootstrap logger + full pipeline (console + rolling daily file under data path)
- [x] Graceful shutdown: `OperationCanceledException` cascades through all services; `ShuttingDown` status reported to hub; `IServerLink.DisposeAsync` cleans up connection
- [x] `AgentHub.RegisterAsync` fixed: only overwrites server-side roles when agent sends a non-empty list (preserves wizard-configured roles otherwise)

### Phase 9 — Live target status in the UI

- [x] `UiHub` + `IUiHubClient` at `/hubs/ui` — SignalR hub for external browser clients; pushes `TargetStatusChangedAsync(targetId, status, lastSeenUtc)` to all connected clients
- [x] `ITargetStatusNotifier` / `InMemoryTargetStatusNotifier` — in-process event bus for Blazor Server circuits (avoids second network round-trip from component)
- [x] `TargetStatusPublisher` singleton — single `PublishAsync` call fans out to both in-process notifier and UI hub context
- [x] `AgentHub` injects `TargetStatusPublisher`; calls `PublishAsync` on `OnConnectedAsync` (Online) and in `MarkOfflineAfterGraceAsync` (Offline)
- [x] `Targets.razor` subscribes to `ITargetStatusNotifier.TargetStatusChanged` in `OnAfterRender(firstRender)`, unsubscribes in `IAsyncDisposable.DisposeAsync`; mutates the target in `_targets` in-place and calls `InvokeAsync(StateHasChanged)`
- [x] Status badge wraps a `<span title="Last seen …">` tooltip; Last Seen column also has the ISO timestamp as title

### Phase 10 — Logging and telemetry skeleton

- [x] Serilog on the server: bootstrap logger → `UseSerilog` (console + rolling daily file `logs/server-.log`); `Enrich.FromLogContext`, `WithMachineName`, `WithThreadId`; `ReadFrom.Configuration` picks up level overrides from the `Serilog` section in appsettings; `UseSerilogRequestLogging` logs each HTTP request with method, path, status, and elapsed ms
- [x] Serilog on the agent was completed in Phase 8 (bootstrap logger + `AddSerilog` with console + rolling file)
- [x] `RequestId` available via `Enrich.FromLogContext()` (set by `UseSerilogRequestLogging`); `AgentId` and `DeploymentId` unused in M1 but pushable via `LogContext.PushProperty`
- [x] OpenTelemetry: tracing + metrics wired via `AddOpenTelemetry()` with `AddAspNetCoreInstrumentation()` + `AddHttpClientInstrumentation()`; console exporter enabled in Development only; service name `KrakenDeploy.Server` set via `ConfigureResource`
- [x] `IAgentConnectionRegistry.Count` added; `InMemoryAgentConnectionRegistry` returns `_byConnection.Count`
- [x] `/healthz` now includes `connectedAgents = registry.Count` alongside Postgres ping and total target count

### Phase 11 — Dev experience

- [x] `README.md` filled in:
  - prerequisites (.NET 9 SDK, Docker, pwsh)
  - `docker compose up -d postgres`
  - `dotnet ef database update --project src/KrakenDeploy.Server.Data --startup-project src/KrakenDeploy.Server.Data`
  - `dotnet run --project src/KrakenDeploy.Server -- users create-admin --email you@example.com --password ...`
  - `dotnet run --project src/KrakenDeploy.Server`
  - agent run instructions with registration token
  - `/healthz` check documented
  - scripts reference table
- [x] `launchSettings.json` for server (HTTPS on port 5443, HTTP on port 5080) and agent (`Server__Url` env var pointing to localhost:5443)
- [x] `scripts/` folder with cross-platform PowerShell scripts (pwsh runs on both): `build.ps1`, `run-server.ps1`, `run-agent.ps1`, `migrate.ps1`, `reset-db.ps1`, `create-admin.ps1`
- [x] `appsettings.Development.json` checked in with placeholder values; real secrets via user-secrets (documented in README)

### Phase 12 — CI

- [ ] `.github/workflows/ci.yml`: triggers on push + PR; matrix on `ubuntu-latest` and `windows-latest`; restore → build (TreatWarningsAsErrors catches drift) → test; upload test results
- [ ] Defer: container image build, signing, releases, dependency scanning

### Phase 13 — Tests for M1

- [ ] Unit: entity invariants (Project slug rules, Environment ordering)
- [ ] Integration (Postgres testcontainer): migrations apply cleanly, basic Project CRUD via repo, idempotent
- [ ] Hub test: `AgentHub.RegisterAsync` updates target row and notifies UI hub group
- [ ] Agent test: registration host service exchanges one-time token correctly against a fake server
- [ ] **Cross-platform smoke (CI):** docker-compose with server in one container and agent in a Linux container; assert the target goes Online — this is the real M1 exit-criterion check

---

## Future milestones (sketches)

### M2 — first real deployment
Package upload to server, manual release creation, gRPC channel from agent, full-package transfer on deploy command, single built-in step `Octopus.Script` (PowerShell on Windows, Bash on Linux), live log streaming agent → server → UI.

### M3 — variables and Octostache
Variable sets, scoped variables, scope resolution. Octostache integration in step inputs and scripts. **`StringArray`** type with iteration syntax and dual `$OctopusParameters` / `$OctopusArrays` exposure. Sensitive variable encryption.

### M4 — Octodiff and large packages
Local package cache on agent. Server-side Octodiff signature/delta generation. Resumable chunked transfer with ACKs. Benchmark vs. plain transfer; tune chunk size.

### M5 — Octopus step-template compatibility
Importer for step-template JSON from the Library repo. Parameter-controlType mapping to Radzen form controls. Day-one handlers: `Octopus.Script`, `Octopus.IIS` (basic), `Octopus.WindowsService`, `Octopus.FileTransform`, `Octopus.SubstituteVariables`, `Octopus.Manual`. Kraken PowerShell module shipped to agents with cross-platform helpers and Octopus-compat aliases.

### M6 — multi-tenancy and tags
Tenants, Tag Sets, tenant tags, tag-set scoping for variables and deployment targets. Project-tenant connections. Tenant common variables and per-project tenant variables.

### M7 — channels, lifecycles, retention, runbooks
Channels with version rules and per-channel lifecycles. Lifecycle phases with optional/required environments and gates. Release retention per phase. Package retention per feed. Runbooks (no-release automation reusing the step engine).

### M8 — offline drop targets
Drop bundle generation. Result bundle ingestion (manual upload, auto-email, HTTP POST webhook, SFTP/file-share polling). `PendingOfflineResult` deployment status. HMAC signing per target. UI for re-cutting drops and previewing inbound results.

### M9 — Kraken.IIS comprehensive
Full superset action type covering app pool process model + recycle settings (incl. `loadUserProfile` and recycle event log entry flags), rapid-fail protection, identity, complete site bindings (cert from variable), application init/preload, URL Rewrite, request filtering, response headers, MIME types, default documents, virtual directories, sub-applications, atomic-swap deploy with rollback, drain-mode recycle, post-deploy health probe.

### M10 — operational polish
Direct + polling transport modes. Hangfire scheduled deployments and retention. Audit log, RBAC, API tokens. Agent auto-update. OIDC SSO. OpenTelemetry export to Grafana stack or Seq. Caddy reverse-proxy reference deployment.

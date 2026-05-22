# KrakenDeploy — Task List

A self-hosted, .NET-native deployment platform inspired by Octopus Deploy. This file tracks work milestone-by-milestone.

## Project status: pre-production — breaking changes allowed

**Not yet deployed to any real installation.** While that holds, **prefer clean redesigns over back-compat shims** — rename freely, drop columns, reshape JSON, change step-type names, retire endpoints. See [docs/architecture.md](docs/architecture.md#project-status-pre-production-breaking-changes-allowed) for the full policy. The first day KrakenDeploy ships to a customer, this section gets deleted and v1 contracts freeze.

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

- [x] `.github/workflows/ci.yml`: triggers on push + PR; matrix on `ubuntu-latest` and `windows-latest`; restore → build (TreatWarningsAsErrors catches drift) → test; upload test results; `concurrency` cancels stale in-progress runs; `TESTCONTAINERS_RYUK_DISABLED=true` for Windows runner stability
- [x] Defer: container image build, signing, releases, dependency scanning

### Phase 13 — Tests for M1

- [x] Unit: entity invariants (Project slug rules, Environment ordering)
- [x] Integration (Postgres testcontainer): migrations apply cleanly, basic Project CRUD via repo, idempotent
- [x] Hub test: `AgentHub.RegisterAsync` updates target row and notifies UI hub group
- [x] Agent test: registration host service exchanges one-time token correctly against a fake server
- [x] **Cross-platform smoke (CI):** docker-compose with server in one container and agent in a Linux container; assert the target goes Online — this is the real M1 exit-criterion check

---

## Future milestones (sketches)

### M2 — first real deployment

**Exit criterion.** Upload a zip package, define a one-step Script process, create a release, trigger a deployment — the agent downloads the package via gRPC, extracts it, runs the script, streams every log line back to the server in real time, and the deployment completes Succeeded or Failed with a full log visible in the UI.

#### Phase 14 — Package management

- [x] `Package` domain entity (`PackageId`, `Version`, `FileName`, `StoredPath`, `SizeBytes`, `UploadedUtc`)
- [x] `IPackageStore` abstraction + `LocalPackageStore` (stores at `{dataPath}/packages/{id}/{ver}/{file}`)
- [x] `PackageConfiguration` EF config (table `packages`, unique index on `(package_id, version)`)
- [x] `PackageService` — `UploadAsync` (with `MeasuredStream` byte counter), `GetSummariesAsync`, `GetVersionsAsync`, `GetAsync`, `DeleteAsync`
- [x] REST: `POST /api/packages/upload`, `GET /api/packages`, `GET /api/packages/{id}/versions`, `DELETE /api/packages/{id:guid}`

#### Phase 15 — Deployment process & steps

- [x] `DeploymentProcess` + `DeploymentStep` domain entities (one-to-one with Project, ordered by `SortOrder`)
- [x] EF configs for `deployment_processes` and `deployment_steps` (jsonb `Config`, `text[]` `TargetRoles`)
- [x] `ProcessService` — `GetOrCreateAsync`, `AddStepAsync` (appends with `maxSort+1`), `UpdateStepAsync`, `RemoveStepAsync` (re-sequences)
- [x] REST: `GET+POST /api/projects/{id}/process/steps`, `DELETE /api/projects/{id}/process/steps/{stepId}`

#### Phase 16 — Releases with process snapshot

- [x] `StepSnapshot` value object (immutable copy of a step at release time)
- [x] `Release.ProcessSnapshot` (`List<StepSnapshot>` stored as jsonb) + `Release.ReleaseNotes`
- [x] `ReleaseService.CreateAsync` — validates uniqueness, snapshots process, pins package versions (explicit map or latest uploaded)
- [x] REST: `GET+POST /api/projects/{id}/releases`

#### Phase 17 — gRPC package delivery channel

- [x] `kraken.proto`: `PackageDelivery.Download` server-streaming RPC returning 64 KB `DownloadChunk` messages with `IsLast` marker
- [x] Contracts project: `GrpcServices="Both"` codegen + `Grpc.Net.Client` dependency; `Grpc.AspNetCore` added to Server
- [x] `GrpcPackageDeliveryService` (server): streams file in 64 KB chunks, `[Authorize(AuthenticationSchemes="AgentJwt")]`
- [x] `GrpcPackageDownloader` (agent-side): lazily creates channel per server URL, bearer token in default headers, `AppContext.SetSwitch` for HTTP/2 cleartext

#### Phase 18 — Deployment orchestration

- [x] `DeploymentLogEntry` entity (per-deployment monotonic `Sequence`, `Level`, `Message`, `Timestamp`)
- [x] `DeploymentService.CreateAsync` — validates, creates `Deployment` (Queued), writes ID to `Channel<Guid>`
- [x] `DeploymentWorker` background service — reads channel, loads deployment + release + snapshot, builds `DeploymentPlan`, marks Running, sends `RunDeploymentAsync(plan)` to agent via `IHubContext<AgentHub, IAgentHubClient>`; marks Failed if target offline
- [x] `AgentHub.AppendLogAsync` — allocates `NextLogSequence`, persists `DeploymentLogEntry`, broadcasts to `UiHub` group `deployment:{id}`
- [x] `AgentHub.CompleteDeploymentAsync` — transitions `Succeeded`/`Failed`, broadcasts status change
- [x] `IUiHubClient` extended with `DeploymentLogAppendedAsync` and `DeploymentStatusChangedAsync`

#### Phase 19 — Agent execution engine

- [x] `ScriptRunner` — writes script to temp file, spawns `pwsh -NonInteractive -NoProfile` / `bash`, captures stdout→`info` and stderr→`error` via `OutputDataReceived`/`ErrorDataReceived`
- [x] `PackageExtractor` — static `ExtractAsync` wraps `ZipFile.ExtractToDirectory` in `Task.Run`
- [x] `DeploymentExecutor` — per-step: log header → download → extract → validate step type → inject env vars → run script → log result → cleanup staging
- [x] `IServerLink` extended: `AppendLogAsync`, `CompleteDeploymentAsync`, `OnRunDeployment(Func<DeploymentPlan,Task>)`
- [x] `SignalRServerLink` — pre-registers handlers before hub start; `OnRunDeployment` list allows late registration
- [x] `ServerLinkHostedService` — wires `OnRunDeployment` before `StartAsync` so no messages are dropped
- [x] EF migration `AddM2Schema` — new tables + `process_snapshot`/`release_notes`/`next_log_sequence` columns

### M2.5 — `kraken` CLI ✅

- [x] `KrakenDeploy.Cli` project (`dotnet tool` + single-file self-contained binaries)
- [x] Auth via `KRAKEN_SERVER` + `KRAKEN_API_KEY` env vars or `--server` / `--api-key` flags; `ApiKeyAuthenticationHandler` on server
- [x] `package create` — zip a publish directory
- [x] `package upload` — multipart POST to `/api/packages/upload`
- [x] `release create` — POST to `/api/projects/{id}/releases`
- [x] `release deploy [--wait] [--timeout]` — POST then poll `/api/deployments/{id}/logs`; tails log to stdout; exits with deployment exit code
- [x] `release list`, `target list`, `target health`
- [x] `--wait` gate usable in any CI pipeline

### M3 — variables and Octostache ✅

- [x] `VariableSet` + `Variable` domain entities; `VariableType` enum (String, Sensitive, StringArray)
- [x] `VariableScope` jsonb column (EnvironmentId, TargetId, Roles)
- [x] `VariableService.ResolveAsync` — scope priority (env+target+role > env+target > env+role > target+role > env > target > role > unscoped)
- [x] AES-256-GCM encryption for Sensitive variables (`AesEncryptionService`, `Encryption:MasterKey`)
- [x] **`StringArray`** type — stored as `text[]`; comma-joined for Octostache; `$OctopusArrays` hashtable in PowerShell preamble
- [x] Octostache `#{VarName}` substitution injected into PowerShell preamble via `$OctopusParameters`
- [x] Variable Sets UI page; REST API; EF migration `AddM3Schema`

### M4 — Octodiff and large packages ✅

- [x] `IPackageCache` / `LocalPackageCache` on agent (`{dataPath}/package-cache/`)
- [x] `PackageDeltaService` — server-side Octodiff signature + delta generation, signatures cached alongside package files
- [x] `GrpcPackageDownloader` — cache-hit → delta → full-download priority; `resume_offset` for resumable full transfers
- [x] `DownloadRequest.base_version` + `DownloadChunk.is_delta` proto fields
- [x] EF migration included in `AddM2Schema`

### M5 — Octopus step-template compatibility ✅

- [x] `StepTemplate` + `StepTemplateParameter` domain entities (jsonb `Properties` and `Parameters`)
- [x] `OctopusLibraryImporter.Parse` — accepts Library JSON (`"Id"` key) and Octopus API JSON (`"CommunityActionTemplateId"` key); trailing commas; all 5 control types; full `"value|Label"` SelectOptions preserved
- [x] `StepTemplateService` — CRUD + `ImportFromJsonAsync` (upsert by community id)
- [x] `IStepHandler` dispatch pattern in `DeploymentExecutor` (replaces hard-coded script runner)
- [x] Handlers: `ScriptStepHandler` (`Kraken.Script` + `Octopus.Script`), `SubstituteVariablesStepHandler`, `FileTransformStepHandler`, `ManualInterventionStepHandler`
- [x] PowerShell preamble: `$OctopusParameters`, `$OctopusArrays`, `Write-KrakenInfo/Warning/Error`, `Get-KrakenVariable`, `Register-KrakenArtifact` (stub — completed in M5.5), Octopus-compat `Set-Alias`
- [x] Step Templates UI page (import JSON, create, delete); `ImportStepTemplateDialog`, `CreateStepTemplateDialog`
- [x] REST API for step templates; EF migration `AddM5Schema`
- [x] 11 `OctopusLibraryImporterTests` + 31 `StepHandlerTests`

### M5.5 — deployment artifacts ✅

- [x] `DeploymentArtifact` entity (StepName, FileName, ContentType, SizeBytes, StoredPath, CollectedUtc)
- [x] `IArtifactStore` / `LocalArtifactStore` — `{dataPath}/artifacts/{deploymentId}/{stepName}/{file}`
- [x] `ArtifactService` — Save, List, OpenRead, Delete
- [x] Proto: `ArtifactUpload` service, `Upload(stream ArtifactChunk) → ArtifactUploadResult`
- [x] `GrpcArtifactUploadService` (server-side, `AgentJwt` auth)
- [x] `GrpcArtifactUploader` (agent-side, 64 KB chunks, channel reuse)
- [x] `StepHandlerContext.ArtifactsDir`; `KRAKEN_ARTIFACTS_PATH` env var set per step
- [x] `DeploymentExecutor` scans artifacts dir and uploads after every step
- [x] `Register-KrakenArtifact` copies file into artifacts dir (was stub in M5)
- [x] REST: `GET /api/deployments/{id}/artifacts`, `GET /api/deployments/{id}/artifacts/{artifactId}/download`
- [x] `Deployments.razor` — real data grid with status badges, row-click to detail
- [x] `DeploymentDetail.razor` (`/deployments/{id}`) — info card, log tab, artifacts tab with image thumbnails and download links
- [x] EF migration `AddM55Schema`

### M6 — multi-tenancy and tags ✅

- [x] Domain: `Tenant` (Slug, Name, Description, VariableSetId?, TagSets nav, Projects nav)
- [x] Domain: `TagSet` (TenantId, Name, Description, SortOrder, Tags nav)
- [x] Domain: `TenantTag` (TagSetId, Name, Color?, Targets nav)
- [x] `Project.Tenants` many-to-many nav; `DeploymentTarget.TenantTags` many-to-many nav
- [x] `Deployment.TenantId?` + `Tenant?` navigation
- [x] `VariableScope.TenantId?` — +8 specificity; `Matches` accepts optional `tenantId`
- [x] EF configurations: `TenantConfiguration`, `TagSetConfiguration`, `TenantTagConfiguration`; join tables `project_tenants`, `target_tenant_tags`
- [x] `KrakenDbContext` — DbSets for Tenant, TagSet, TenantTag
- [x] `TenantService` — full CRUD for tenants, tag sets, tags; project-tenant and target-tag connections
- [x] `VariableService.ResolveAsync` — `tenantId?` parameter; resolves tenant common variable set first, then project set overlays
- [x] `DeploymentService.CreateAsync` — accepts `tenantId?`; validates tenant exists
- [x] `TriggerDeploymentRequest` — `TenantId?` field; `UpsertVariableRequest` — `ScopeTenantId?` field
- [x] `DeploymentWorker` passes `deployment.TenantId` to `ResolveAsync`
- [x] REST: tenants CRUD, project-tenant connect/disconnect, tag-set CRUD, tag CRUD, target-tag add/remove, target tags list
- [x] EF migration `AddM6Schema` (tenants, tag_sets, tenant_tags, project_tenants, target_tenant_tags, deployments.tenant_id)
- [x] `TenantService` registered in `ServiceCollectionExtensions`
- [x] `Tenants.razor` — list with create inline form, delete, row-click to detail
- [x] `TenantDetail.razor` (`/tenants/{id}`) — tag set CRUD + per-set tag CRUD with color dots

### M7 — channels, lifecycles, retention, runbooks ✅

- [x] Domain: `Lifecycle` (Name, Description, `List<LifecyclePhase>` stored as JSONB)
- [x] Domain: `LifecyclePhase` value object (Id, Name, SortOrder, EnvironmentIds, OptionalEnvironmentIds, MinimumEnvironments, IsOptional, RetentionKeepDeployments)
- [x] Domain: `Channel` (ProjectId, Name, IsDefault, LifecycleId?, VersionRange?, VersionTag?)
- [x] `Release.ChannelId?` + `Channel?` navigation; `Project.LifecycleId?` + `Lifecycle?` + `Channels` nav
- [x] EF configurations: `LifecycleConfiguration`, `ChannelConfiguration`; updated `ReleaseConfiguration`, `ProjectConfiguration`
- [x] `LifecycleService` — CRUD; `UpdateAsync` re-assigns SortOrder and ensures phase IDs
- [x] `ChannelService` — `GetOrCreateDefaultAsync`; `ExecuteUpdateAsync` to clear old default; delete guard
- [x] Lifecycle gate enforcement in `DeploymentService.CreateAsync` — checks all required earlier phases have `minRequired` successful deployments for the release
- [x] `CreateReleaseRequest.ChannelId?`; `ReleaseService.CreateAsync` assigns channel
- [x] Domain: `Runbook`, `RunbookProcess`, `RunbookStep`, `RunbookRun` (reuses `DeploymentStatus`), `RunbookRunLogEntry`
- [x] `RunbookRun.ProcessSnapshot` — `List<StepSnapshot>` captured at trigger time (JSONB)
- [x] EF configurations: `RunbookConfiguration`, `RunbookRunConfiguration`
- [x] `KrakenDbContext` — DbSets for Lifecycles, Channels, Runbooks, RunbookProcesses, RunbookSteps, RunbookRuns, RunbookRunLogEntries
- [x] `RunbookService` — full CRUD + step management + `TriggerAsync` (snapshot + enqueue)
- [x] `RunbookRunChannel` singleton wrapper (avoids DI ambiguity with deployment `Channel<Guid>`)
- [x] `RunbookRunWorker` BackgroundService — dispatches `DeploymentPlan` (run ID as DeploymentId) to target agent
- [x] `AgentHub.AppendLogAsync` — tries Deployment table, then RunbookRun table (zero protocol change)
- [x] `AgentHub.CompleteDeploymentAsync` — same dual-table lookup; calls fire-and-forget `RetentionService.PruneAfterDeploymentAsync` on success
- [x] `RetentionService.PruneAfterDeploymentAsync` — prunes excess successful deployments per lifecycle phase
- [x] REST: lifecycle CRUD, channel CRUD, runbook CRUD + step CRUD + trigger + runs query
- [x] EF migration `AddM7Schema` (lifecycles, channels, runbooks, runbook_processes, runbook_steps, runbook_runs, runbook_run_log_entries, releases.channel_id, projects.lifecycle_id)
- [x] `Lifecycles.razor` — list with create inline form, delete, row-click to detail
- [x] `LifecycleDetail.razor` (`/lifecycles/{id}`) — phase management (add/reorder/remove, per-phase environment checkboxes, retention, save whole document)
- [x] `Runbooks.razor` — project-scoped runbook list with create form
- [x] `RunbookDetail.razor` (`/runbooks/{id}`) — Steps tab (add/edit/delete), Runs tab with inline log viewer, Run Now trigger panel
- [x] Runbooks nav item added to sidebar

### M8 — offline drop targets ✅

Drop bundle generation. Result bundle ingestion (manual upload, auto-email, HTTP POST webhook, SFTP/file-share polling). `PendingOfflineResult` deployment status. HMAC signing per target. UI for re-cutting drops and previewing inbound results.

**Completed:**
- [x] `TransportMode.OfflineDrop` enum value
- [x] `OfflineDropConfig` JSONB model (delivery channel, HMAC key, SMTP, webhook, file-share settings)
- [x] `OfflineDropDeliveryChannel` enum (Manual, Email, Webhook, FileShareDrop)
- [x] `DropBundleService` — generates self-contained zip bundles with manifest, variables, packages, orchestrator scripts (PS + Bash), HMAC signature
- [x] `OfflineResultService` — ingests result bundles: HMAC verification, status parsing, log parsing, artifact extraction
- [x] `DeploymentWorker` dispatches offline-drop deployments: generates bundle → `PendingOfflineResult`, delivers via webhook/file-share if configured
- [x] API endpoints: `GET /api/deployments/{id}/drop-bundle`, `POST /api/deployments/{id}/offline-result`
- [x] `DeploymentDetail.razor` — download bundle, upload result, re-generate, status display
- [x] `AddTargetWizardDialog` — OfflineDrop transport option with no-agent confirmation
- [x] `TargetDetail.razor` — offline drop config editor (delivery channel, HMAC key management, SMTP/webhook/file-share settings)
- [x] `Targets.razor` — name links to detail page, transport mode column
- [x] `Deployments.razor` — PendingOfflineResult badge style
- [x] `TargetRegistrationService` — offline-drop targets created without registration token
- [x] EF migration `AddM8Schema` (drop_bundle_path, offline_drop_config JSONB)
- [x] DI registration of `DropBundleService` and `OfflineResultService`

### M9 — Kraken.IIS comprehensive ✅

Full superset action type covering app pool process model + recycle settings (incl. `loadUserProfile` and recycle event log entry flags), rapid-fail protection, identity, complete site bindings (cert from variable), application init/preload, URL Rewrite, request filtering, response headers, MIME types, default documents, virtual directories, sub-applications, atomic-swap deploy with rollback, drain-mode recycle, post-deploy health probe.

**Completed:**
- [x] `KrakenIisConfigKeys` — flat string keys for the entire IIS configuration surface (general, app pool, recycling, rapid-fail, bindings, preload, deploy, health)
- [x] `KrakenIisConfig` strongly-typed parsed view with `Parse()` from step config dictionary; sub-records `KrakenIisAppPool`, `KrakenIisRecycle`, `KrakenIisRapidFail`, `KrakenIisBinding`, `KrakenIisDeploy`, `KrakenIisHealthCheck`
- [x] `KrakenIisBinding` with pipe-delimited line parser (HTTP/HTTPS, SNI, cert thumbprint + store, SSL flags)
- [x] `KrakenIisStepHandler` registered in agent DI; claims both `Kraken.IIS` and `Octopus.IIS` step types; Windows-only with clean error on non-Windows
- [x] `IisScriptGenerator` produces idempotent PowerShell using `WebAdministration` module:
  - App pool ensure + full process model (runtime, pipeline, 32-bit, loadUserProfile, identity incl. SpecificUser)
  - App pool recycling (regular interval, private/virtual memory limits, request limit, specific times, all 7 log-event flags)
  - Rapid-fail protection (enabled, max crashes, interval)
  - Site ensure with placeholder, then bindings replaced; HTTPS bindings bind cert from store with SNI and SSL flags
  - Application preload + always-running
  - **Atomic-swap deploy** with versioned subdirectories under WebRoot, physicalPath swap, retention of N old versions
  - In-place deploy alternative
  - **Drain-mode recycle** (overlapping) by default, hard recycle option
  - **Post-deploy HTTP health probe** with retries, expected status, expected body fragment
- [x] Generated PowerShell script saved as `kraken-iis-deploy.ps1` in step artifacts directory for troubleshooting
- [x] `BuiltInStepTemplateSeeder` seeds the `Kraken.IIS — Deploy Web Site` template on startup with 38 form parameters (text, select, checkbox, sensitive, multiline) so users get a Radzen form picker
- [x] Server.Data project references Contracts (for shared step config keys)
- [x] Unit tests: 17 new tests covering required-key validation, defaults, full config parse, binding parser (HTTP, HTTPS, multiline, comments), handler step-type matching

### M10 — operational polish + Spaces foundation

**Deployment-model strategy:** path **B** — both on-prem and cloud SaaS, on-prem first. M10 delivers the shared baseline that both scenarios need; M10.1 packages the on-prem product; M10.2 hardens for cloud SaaS. The Spaces entity is added in M10 (cheap now, painful later) so the cloud milestone doesn't have to migrate live tenant data.

#### Implementation slice tracker

The atomic-commit plan for M10. **Resilient to context loss** — if a session is cut mid-flight, this list plus `git log --grep="M10"` is enough to figure out what's done and what's next without re-deriving the plan from conversation memory.

| Slice | Done | Description |
|---|---|---|
| Spaces foundation | ✅ `bfc1bd4` | `Space` entity + `ISpaceScoped` marker + EF Core global query filter + `AddSpacesFoundation` migration. |
| Spaces tests | ✅ `2a791f7` | Reflection sweep that fails the build if any new top-level aggregate forgets `ISpaceScoped`. |
| `HttpSpaceContext` + `SpaceService` + Switcher | ✅ `de59b44` | `kraken-active-space` cookie, `/space/switch` endpoint, Radzen `<SpaceSwitcher>` (hidden when only Default Space exists). |
| **A1** — Permission enum + ProjectGroup | ✅ `d199c04` | 105-member `Permission` enum with stable integer values + `PermissionTests` lock-in. `ProjectGroup` entity + Project FK. |
| **A2** — RBAC entities + migration | ✅ `76d41cb` | `Role`, `Team`, `TeamMember`, `TeamExternalGroup`, `RoleAssignment`, `IdentityProvider`. Switched `IdentityDbContext` → `IdentityUserContext` (no Identity-managed roles). `AddRbacFoundation` migration drops unused Identity role tables. |
| **B** — Built-in seeder + IPermissionEvaluator | ✅ `b7ea433` | 8 built-in roles (System Administrator, Space Manager, Project Deployer/Contributor/Viewer, Tenant Manager, Runbook Producer/Consumer). `BuiltInRbacSeeder` runs idempotently per Space. `IPermissionEvaluator` first cut. |
| **B3** — tighten scope matching | ✅ `7ce0b0d` | `RoleAssignmentScopeMatcher` (pure helper) actually enforces per-Project / Environment / Tenant scopes. 12 matcher tests. |
| **C** — Authorization integration | ✅ `78b03d0` | `PermissionPolicyProvider` (dynamic `"perm:{Permission}"` policies), `PermissionAuthorizationHandler`, `RequirePermission` extension, `<RequirePermission>` Blazor component. `users create-admin` CLI auto-adds new admins to "Kraken Administrators". |
| **D** — endpoint call-site migration | ✅ `d9fab2a` | 71 endpoints in `Program.cs` swapped from `.RequireAuthorization()` to `.RequirePermission(Permission.X)`. 4 endpoints intentionally retain auth-only (sign-out, list-Spaces, switch-Space). |
| **E** — Permission-aware UI | ✅ `50a4f93` | `<RequirePermission>` wraps Create/Edit/Delete buttons in Projects, Environments, Targets, TargetDetail, Tenants, Lifecycles, Runbooks, StepTemplates. `Permission` + `Shared` namespaces added to `_Imports.razor`. |
| **F** — Users / Teams / Roles / IdPs config UI | ✅ `305ef92` | New pages under `Configuration` for: list/create/edit Users; list/create/edit Teams (members + external groups + role assignments with scope picker); list/create/edit custom Roles (permission picker grouped by domain, code-behind for Razor generic fix); list/create/edit Identity Providers. |
| **G** — OIDC integration | ✅ `5c25d40` | `OidcRegistrar` loads enabled `IdentityProvider` rows at startup and registers one named OIDC scheme per provider. JIT user provisioning in `OnTicketReceived`. `ApplicationUser` extended with `LastOidcProviderId` + `ExternalGroups` (pipe-sep, survives stamp refresh). `PermissionEvaluator` branch c maps external groups to teams via DB. Login page shows OIDC buttons + error messages. Migration: `AddOidcUserFields`. |
| **H** — Audit log | ✅ `cfcd711` | `AuditEntry` (jsonb snapshots, indexed) + `IAuditLog`/`AuditEventType` in Core. `AuditLogInterceptor` (SaveChangesInterceptor) auto-captures Added/Modified/Deleted with before/after JSON, skips sensitive props. `AuditLogService` for explicit events + `PurgeOldEntriesAsync` (Hangfire hook). `/audit` page: date-range/event-type/user/subject filters, snapshot viewer, `Permission.EventView` gate. Migration: `AddAuditLog`. |
| **I** — Hangfire scheduled work | ✅ `67f9c0a` | `Hangfire.AspNetCore` + `Hangfire.PostgreSql` on Postgres. Four recurring jobs: `AuditRetentionJob` (nightly 03:00), `AgentLastSeenOfflineJob` (every 5 min), `RegistrationTokenExpiryJob` (nightly 02:00), `ScheduledDeploymentDispatchJob` (every min). `Deployment.ScheduledFor?` + `AddScheduledDeployments` migration. `/hangfire` dashboard — `HangfireDashboardAuthFilter` checks `Permission.AdministerSystem`. |
| **J** — Agent auto-update | ✅ `c66b072` | Server hosts agent binaries + `version.json`. Agent compares version on heartbeat; swaps during configurable maintenance window. Per-target opt-out flag. |
| **K** — Direct + Polling transports | ✅ `c66b072` | `DirectServerLink` (LAN-trusted server-to-agent) and `PollingServerLink` (highly restricted networks) implementations of the existing `IServerLink` abstraction. |
| **L** — Caddy reference deployment | ✅ `c66b072` | `deploy/caddy/Caddyfile` + `docker-compose.yml` + README. Auto-HTTPS, SignalR/gRPC long-lived connection tuning. |

After M10 ships, the work continues into M10.1 (on-prem packaging — MSI / deb / rpm / Compose / license) and M10.2 (cloud SaaS hardening — object storage backends, Redis backplane, billing, signup, blue/green).

**Spaces foundation (the load-bearing change for the cloud roadmap):**

- [ ] `Space` entity: `Id`, `Slug` (URL-friendly), `Name`, `Description`, `IsDefault`, `CreatedUtc`, `Status` (Active/Suspended)
- [ ] Add `SpaceId` FK to every space-scoped entity: Project, Environment, DeploymentTarget, Release, Deployment, VariableSet, Variable, Tenant, TagSet, Lifecycle, Channel, Runbook, RunbookRun, StepTemplate, DeploymentArtifact, Package, ApiKey, AuditEntry. Identity tables (`AspNetUsers` etc.) are platform-level — users can be members of multiple Spaces with per-Space roles.
- [ ] `SpaceMembership` entity: `(UserId, SpaceId, Role)` — a user's role within a Space. System admins implicitly have access to all Spaces.
- [ ] EF Core global query filter: every space-scoped query is auto-filtered by the current user's active Space; `ISpaceContext` scoped service holds the resolved `SpaceId`
- [ ] Single-tenant default: a "Default" Space is created on first run; on-prem installs see no Space picker (one Space, transparent); when more than one Space exists the UI exposes a switcher
- [ ] Migration `AddSpacesFoundation` — backfills `SpaceId` on existing rows to the Default Space
- [ ] Slug-based routing optional: `/s/{spaceSlug}/projects/...` (off by default for on-prem; turned on for cloud)
- [ ] Audit log scoped to Space; system events (admin, billing) live in a Platform space
- [ ] **Octopus terminology mapping:** what Octopus calls a "Space" maps to KrakenDeploy `Space`; what Octopus calls a "Tenant" remains the existing `Tenant` entity (a deployment-target customer *within* a Space). Don't conflate them.

**Authentication — local accounts and OIDC coexist:**

- [ ] Local accounts remain the default and bootstrap path (existing `users create-admin` CLI keeps working)
- [ ] OIDC plug: configurable per-deployment, multiple providers (Microsoft Entra / Azure AD, Okta, Google, Auth0, generic OIDC). Uses `Microsoft.AspNetCore.Authentication.OpenIdConnect`.
- [ ] Just-in-time provisioning: first OIDC sign-in creates an `ApplicationUser` with linked `ExternalLogins` row (no pre-creation needed)
- [ ] Admin policy: "Restrict local password login to Administrator role" — tightens to SSO-only for normal users while keeping break-glass admin login
- [ ] Admin policy: "Auto-assign role X to JIT-provisioned users" + "Auto-add to Space Y"
- [ ] Login page: shows "Sign in with {Provider}" buttons + (conditionally) email/password form, depending on policy
- [ ] Per-Space OIDC tenant restriction (cloud SaaS): only accept tokens whose `tid`/`hd`/`iss` matches the Space's allowed list

**RBAC — full Octopus-parity Users / Roles / Teams model (path 2):**

The atom is a `Permission` enum. Roles bundle Permissions. Teams bundle members (users + external SSO group claims). Role Assignments connect a Team to a Role with a composite scope (Project Groups, Projects, Environments, Tenants, Tenant Tags). The same flexibility Octopus offers — same vocabulary, same evaluation semantics — so an Octopus customer's mental model maps 1:1.

**Permission atoms (~80 atoms covering everything currently built in M1–M9). Final enum will live in `KrakenDeploy.Server.Core/Domain/Security/Permission.cs`.**

System / cross-Space:
- `AdministerSystem` — god mode; implies every other permission everywhere
- `ConfigureServer` — server settings, license, OIDC config, agent auto-update settings
- `SpaceView`, `SpaceCreate`, `SpaceEdit`, `SpaceDelete`
- `UserView`, `UserEdit`, `UserInvite`, `UserChangePassword`
- `TeamView`, `TeamCreate`, `TeamEdit`, `TeamDelete`
- `RoleView`, `RoleCreate`, `RoleEdit`, `RoleDelete` (custom roles)
- `EventViewUnscoped` — full audit log across Spaces

Project / process:
- `ProjectGroupView`, `ProjectGroupCreate`, `ProjectGroupEdit`, `ProjectGroupDelete`
- `ProjectView`, `ProjectCreate`, `ProjectEdit`, `ProjectDelete`
- `ProjectExport`, `ProjectImport`
- `ProcessView`, `ProcessEdit` (the deployment-process step list)

Release / deployment:
- `ReleaseView`, `ReleaseCreate`, `ReleaseEdit`, `ReleaseDelete`
- `DeploymentView`, `DeploymentCreate`, `DeploymentDelete`
- `ArtifactView`, `ArtifactDownload`, `ArtifactCreate`, `ArtifactDelete`
- `OfflineResultUpload` (offline-drop result ingest — M8)

Environment / target:
- `EnvironmentView`, `EnvironmentCreate`, `EnvironmentEdit`, `EnvironmentDelete`
- `MachineView`, `MachineCreate`, `MachineEdit`, `MachineDelete` (DeploymentTarget)
- `MachineRetire` (mark target offline / disable)

Variables (sensitive ones are explicit, mirroring Octopus):
- `VariableView`, `VariableEdit`
- `VariableViewUnscoped`, `VariableEditUnscoped` — sensitive variables (decrypted)
- `LibraryVariableSetView`, `LibraryVariableSetCreate`, `LibraryVariableSetEdit`, `LibraryVariableSetDelete`

Lifecycle / channel:
- `LifecycleView`, `LifecycleCreate`, `LifecycleEdit`, `LifecycleDelete`
- `ChannelView`, `ChannelCreate`, `ChannelEdit`, `ChannelDelete`

Tenants:
- `TenantView`, `TenantCreate`, `TenantEdit`, `TenantDelete`
- `TagSetView`, `TagSetCreate`, `TagSetEdit`, `TagSetDelete`

Runbooks:
- `RunbookView`, `RunbookEdit`
- `RunbookRunView`, `RunbookRunCreate`, `RunbookRunDelete`

Step templates:
- `StepTemplateView`, `StepTemplateCreate`, `StepTemplateEdit`, `StepTemplateDelete`

Package library:
- `PackageView`, `PackageEdit` (upload), `PackageDelete`

Tasks / interruptions:
- `TaskView`, `TaskCancel`, `TaskEdit`, `TaskRerun`
- `InterruptionView`, `InterruptionViewSubmitResponsible` (approve / reject manual intervention)

Audit (within scope):
- `EventView` — audit entries within the scopes the user has access to

API keys:
- `ApiKeyView`, `ApiKeyCreate`, `ApiKeyEdit`, `ApiKeyDelete` (own keys)
- `ApiKeyViewAll`, `ApiKeyDeleteAll` (admin: see/revoke any user's keys)

OIDC / external auth:
- `IdentityProviderView`, `IdentityProviderCreate`, `IdentityProviderEdit`, `IdentityProviderDelete`

(Reserved for future features so the enum doesn't churn: `WorkerView/Create/Edit/Delete`, `WorkerPoolView/Create/Edit/Delete`, `AccountView/Create/Edit/Delete`, `CertificateView`, `CertificateExportPrivateKey`, `SubscriptionView/Create/Edit/Delete`. Add when the entity lands.)

**Domain entities for the access-control model:**

- [ ] `ProjectGroup` entity (new): `Id`, `SpaceId`, `Slug`, `Name`, `Description`, `SortOrder`. Project gets a nullable `ProjectGroupId` FK. Default group "Default Project Group" auto-created per Space. Surfaces in the Projects page as a folder grouping.
- [ ] `Permission` C# enum — the ~80 atoms listed above. Backed by an integer column when persisted (no permission strings on the wire — typed everywhere).
- [ ] `Role` entity: `Id`, `SpaceId` (nullable — null = system role), `Name`, `Description`, `IsBuiltIn` (built-ins can't be renamed/deleted), `GrantedPermissions` (List<Permission> stored as `int[]` in jsonb), `SupportedScopes` (which scope dimensions the role accepts — e.g. `AdministerSystem` ignores all scope), `CanBeAssignedToTenants` (bool — Octopus terminology), `CreatedUtc`, `ModifiedUtc`.
- [ ] `Team` entity: `Id`, `SpaceId` (nullable — null = system team that lives outside any Space), `Name`, `Description`, `IsBuiltIn`, `CreatedUtc`, `ModifiedUtc`.
- [ ] `TeamMember` join table: `(TeamId, UserId)` — explicit user membership.
- [ ] `TeamExternalGroup` entity: `Id`, `TeamId`, `IdentityProviderId` (which OIDC config), `GroupClaim` (the claim value, e.g. an AD group SID, an Entra group ObjectId, an Okta group name), `DisplayName`. When a user signs in via SSO, group claims in the token are matched here to compute dynamic team membership for that session.
- [ ] `RoleAssignment` entity: `Id`, `TeamId`, `RoleId`, scope columns:
  - `ProjectGroupIds` (`Guid[]` jsonb — empty = all project groups in the space)
  - `ProjectIds` (`Guid[]` jsonb — empty = all projects)
  - `EnvironmentIds` (`Guid[]` jsonb — empty = all environments)
  - `TenantIds` (`Guid[]` jsonb — empty = all tenants)
  - `TenantTagIds` (`Guid[]` jsonb — match tenants carrying any of these tags; OR-ed with TenantIds)

  **Scope evaluation rule (matches Octopus):** dimensions are AND-ed; values within a dimension are OR-ed. Empty dimension = "all". If a role doesn't support a dimension (e.g. `LibraryVariableSetEdit` is not environment-scopable), the dimension is ignored during evaluation.

- [ ] `IdentityProvider` entity: `Id`, `Name`, `Type` (Local / Oidc / Saml — Saml later), `IsEnabled`, `Authority`, `ClientId`, `ClientSecretEncrypted`, `Scopes`, `GroupsClaim` (which token claim contains group IDs — defaults to `groups`), `EmailClaim`, `UsernameClaim`, `IsBuiltIn` (Local IdP is built-in), `CreatedUtc`, `ModifiedUtc`. One row per configured provider; Local is always row 1.

**Built-in Roles (auto-seeded, can't be deleted):**

System roles (`SpaceId = null`):
- `SystemAdministrator` — every permission, ignores scope
- `SystemReadOnly` — every `*View` permission across the system

Space roles (one set per Space, conceptually built-in but seeded per-Space):
- `SpaceManager` — every permission within the Space (excludes system perms)
- `ProjectDeployer` — view projects + create releases + create deployments + view variables + run runbooks; scope: typically environments
- `ProjectContributor` — edit projects/process/variables but no `DeploymentCreate`; scope: typically projects
- `ProjectViewer` — `*View` permissions for projects, releases, deployments, variables (non-sensitive)
- `TenantManager` — full tenant CRUD + tenant variables
- `RunbookConsumer` — view runbooks + run runbooks (read + execute, no edit)
- `RunbookProducer` — full runbook edit + run

**Built-in Teams (auto-seeded, can't be deleted):**

System teams (`SpaceId = null`):
- `KrakenDeploy Administrators` — auto-assigned `SystemAdministrator` role; the bootstrap user from `users create-admin` lands here
- `Everyone` — every authenticated user is implicitly a member; assignable to read-only roles

Per-Space teams (auto-created per Space):
- `Space Managers` — assigned `SpaceManager` role for that Space
- `Project Deployers`, `Project Contributors`, `Project Viewers` — assigned the matching role with empty (= all) scope
- `Everyone (Space)` — every Space member; assignable to space-wide read-only roles

**Permission evaluation pipeline:**

- [ ] `IPermissionEvaluator` service:
  - `bool HasPermission(ClaimsPrincipal user, Permission perm, PermissionScope? scope = null)`
  - `IReadOnlySet<Permission> GetEffectivePermissions(ClaimsPrincipal user, PermissionScope scope)` — for "what can I do here?" UI hints
  - `IReadOnlySet<Guid> GetAccessibleProjects(ClaimsPrincipal user, Permission perm)` — drives EF query filters so users only see projects they have access to
  - Same for `GetAccessibleEnvironments`, `GetAccessibleTenants`
- [ ] Resolution order (cached per request via `IUserSecurityContext` scoped service):
  1. Resolve user's effective Teams = explicit `TeamMember` rows + dynamic teams from this session's SSO group claims (matched against `TeamExternalGroup` rows)
  2. Walk all `RoleAssignment` rows for those teams
  3. For each, expand `Role.GrantedPermissions` and intersect with `RoleAssignment` scope
  4. Union all matching grants
  5. `AdministerSystem` short-circuits to "yes, everywhere"
- [ ] Authorization integrations:
  - Minimal-API: `.RequirePermission(Permission.ReleaseCreate)` extension method that resolves the scope from route values (`{projectId}`, `{environmentId}`, `{tenantId}`)
  - Blazor: `<RequirePermission Perm="Permission.DeploymentCreate" ProjectId="@Project.Id">…</RequirePermission>` component that conditionally renders children
  - Razor `[Authorize(Policy = "Permission.X")]` attribute support via dynamic policy provider
- [ ] EF Core `IQueryable<T>` filters: `db.Projects.AccessibleBy(currentUser, Permission.ProjectView)` extension methods so list pages only show what the user can see

**Migration of existing call sites:**

- [ ] Audit every `RequireAuthorization()` and `[Authorize]` in the codebase (47 files identified — see grep)
- [ ] Replace each with the matching `RequirePermission(Permission.X)` based on the operation; default for "any authenticated user" cases stays as `RequireAuthorization()`
- [ ] Bootstrap user (the one from `users create-admin`) goes into `KrakenDeploy Administrators` team automatically

**UI:**

- [ ] Configuration → Users page: list, invite, edit, change-password, view team membership
- [ ] Configuration → Teams page: list, create, edit (members + external groups), view assigned roles
- [ ] Configuration → Roles page: list, view (built-ins are read-only), create custom roles, clone existing role as starting point
- [ ] Configuration → Test Permission tool: pick a user + a context (Space, Project, Environment, Tenant) → see their effective permissions and which Role Assignment granted each one. Critical for debugging "why can't user X do Y?"
- [ ] Project / Environment / Tenant detail pages: "Access" tab listing the role assignments scoped to this entity

**API tokens:**

- [ ] Already exist from M2.5; extend with: `SpaceId` scope (a token belongs to one Space), expiry, last-used timestamp, optional IP allowlist
- [ ] UI page under Settings → API Tokens for create/revoke/list

**Audit log:**

- [ ] `AuditEntry` entity: `Id`, `SpaceId` (nullable for platform events), `UserId`, `UserDisplay`, `OccurredUtc`, `EventType` (e.g. `Deployment.Created`, `Variable.Updated`, `Target.Deleted`, `User.SignedIn`), `Subject` (entity type + id), `Before`/`After` JSONB snapshots, `IpAddress`, `UserAgent`
- [ ] EF Core `SaveChangesInterceptor` writes audit entries automatically for tracked entity changes; explicit `IAuditLog.RecordAsync(...)` for non-EF events (login, API call, file download)
- [ ] `/audit` page: filterable by user, event type, date range, entity; export to CSV
- [ ] Retention: configurable, default 365 days; nightly Hangfire purge

**Hangfire scheduled work:**

- [ ] Wire Hangfire on Postgres (already in stack decisions)
- [ ] Scheduled deployments: a Deployment can carry a `ScheduledFor` timestamp; Hangfire picks it up and dispatches at that time
- [ ] Recurring jobs: package retention sweep, audit retention sweep, registration-token expiry cleanup, drop-bundle retention sweep, agent last-seen → `Offline` transition
- [ ] Hangfire dashboard at `/hangfire` (SystemAdmin only)

**Agent auto-update:**

- [ ] Server hosts agent binaries at `/agents/kraken-agent-{rid}.{ext}` with a `version.json` manifest
- [ ] Agent on heartbeat compares own version to server's published agent version; when newer is available downloads to staging dir
- [ ] Maintenance window: agent only swaps to new binary during a configurable window (default 02:00–04:00 local) — avoids killing in-flight deployments
- [ ] On Windows, the service supervisor restarts; on Linux, systemd `Restart=always` handles it
- [ ] Pin override: target-level "do not auto-update" flag for agents under change-control

**Direct + polling transport modes:**

- [ ] `IServerLink` already abstracts the transport; add `DirectServerLink` (server pushes via agent's HTTPS listener — for trusted LAN scenarios) and `PollingServerLink` (agent polls server every N seconds — for highly restricted networks)
- [ ] Per-target transport selection respected end-to-end (already in `TransportMode` enum from M1)

**Caddy reverse-proxy reference deployment (shared between on-prem and cloud):**

- [ ] `deploy/caddy/Caddyfile` — reverse-proxy with auto-HTTPS, gzip/zstd, SignalR/gRPC long-lived connection tuning
- [ ] `deploy/caddy/docker-compose.yml` — Postgres + KrakenDeploy.Server + Caddy with named volumes
- [ ] `deploy/caddy/README.md` — DNS prerequisites, port-80/443 firewall, log rotation, cert auto-renewal verification
- [ ] Note: this is a *reference*, not a constraint; nginx/IIS/Traefik docs follow as time permits

**Out of scope for M10** (moved to M12 and split scenarios below): OpenTelemetry export, on-prem installer packaging, cloud SaaS hardening.

---

### M10.1 — On-prem packaging

**Customer profile:** software sold to a company; their IT installs on their own hardware (Windows or Linux). One install per company. Often air-gapped or behind a corporate proxy. Auth against their AD / Okta / Azure AD.

#### Implementation slice tracker

| Slice | Done | Description |
|---|---|---|
| **1** — CLI dispatch + database | ✅ | `database create` / `database setup` / `database status` CLI subcommands. Extends `Program.Main` dispatch. |
| **2** — Backup/Restore | ✅ | `backup --to` (pg_dump + data dir + manifest) and `restore --from` CLI. Wrapper scripts. |
| **3** — License enforcement | ✅ | RSA-signed JWT license keys. `LicenseService`, `/settings/license` page, `<LicenseWarningBanner>`. |
| **4** — Docker Compose on-prem | ✅ | `deploy/onprem/` stack: Postgres + Server + Caddy + kraken-init. `.env.example`, README. |
| **5** — HA pair (Postgres registry) | ✅ | `PostgresAgentConnectionRegistry` via UNLOGGED table. `AddAgentConnectionRegistry` migration. Conditional DI. `docs/ha-pair.md`. |
| **6** — OIDC config templates | ✅ | `docs/oidc-templates/`: Entra ID, Azure AD, Okta, Google Workspace, ADFS setup guides. |
| **7** — Velopack Windows MSI | ⏳ | **Deferred** — needs code signing cert, release channel, app icon decisions. |
| **8** — Linux .deb/.rpm | ⏳ | **Deferred** — needs maintainer info, GPG key, target distro decisions. |
| **9** — On-prem deployment guide | ✅ | `docs/on-prem-guide.md` covering all install paths, license, OIDC, backup/restore, upgrade, HA. README updated. |
| **10** — Single Space mode | ✅ | SpaceSwitcher hides when only Default Space exists (done in M10). Spaces management page at `/configuration/spaces`. |

Slices 7-8 (Velopack MSI + Linux packaging) are deferred for a separate session — they require user decisions about code signing, package registries, and release channels.

#### Implementation detail

- [x] **Database setup flow** (both standalone and Docker paths — mirrors Octopus Deploy's installer UX):
  - **Recommended path — installer creates the database:** User provides Postgres admin credentials (host, port, superuser name/password) + desired database name. Installer connects, runs `CREATE DATABASE <name>`, then applies EF Core migrations + seeds initial data. No manual DBA work needed.
  - **Manual path — user pre-creates an empty database:** User creates an empty database themselves (e.g. via `createdb` or their DBA), gives Kraken the connection string. Installer runs migrations + seed. If the database already contains Kraken tables, the installer warns and aborts unless the user confirms it's an in-place upgrade.
  - **Docker path:** `docker compose up` brings its own Postgres container — no external DB needed. Migrations run automatically on first startup via an init container or startup hook.
  - EF Core handles all schema creation and ongoing migrations. Kraken owns its schema end-to-end.
- [ ] **Windows MSI installer** (Velopack): bundles the `KrakenDeploy.Server` binaries, walks through the database setup flow above, registers as a Windows Service, opens firewall port, drops Start Menu shortcuts. Uninstaller preserves the database (must be dropped manually if desired).
- [ ] **Linux packaging**: `.deb` and `.rpm` packages with systemd unit; `apt install krakendeploy-server` style. PostgreSQL listed as an external dependency (user brings their own or installs separately). Post-install script runs the database setup flow interactively or via debconf.
- [x] **Docker Compose stack**: `deploy/onprem/docker-compose.yml` — Postgres + Server + Caddy + named volumes for `data/` and `pg-data/`. One-command bring-up.
- [x] **License key enforcement**: signed JWT-style key with claims (`maxTargets`, `maxUsers`, `expiresUtc`, `customerName`); validated on startup and warned in UI when approaching limits or expiring. Air-gapped activation: customer pastes the key, no phone-home required.
- [x] **Backup/restore documentation**: `pg_dump` schedule + `data/` folder rsync; documented restore procedure. CLI helper: `KrakenDeploy.Server backup --to <path>` and `restore --from <path>`.
- [x] **Update path**: documented in-place upgrade (stop service, run new installer, migrations apply on restart). Rollback procedure (restore DB, downgrade binaries).
- [x] **Bundled OIDC config templates** for the common cases: Active Directory (via Microsoft Entra Connect), ADFS, Azure AD, Okta, Google Workspace.
- [x] **HA pair** (optional, for larger customers): two `KrakenDeploy.Server` nodes against shared Postgres, with sticky-session reverse proxy (Caddy `lb_policy` or external load balancer).
  - **SignalR connection registry via Postgres (no Redis):** `IAgentConnectionRegistry` gets a `PostgresAgentConnectionRegistry` implementation backed by an `UNLOGGED` table — no WAL overhead, fast enough for the 2-node HA case. Table is `(connection_id text PK, target_id uuid, connected_at_utc timestamptz)` with the PK as a covering index (index-only scans). Operations map directly: `INSERT` on connect, `DELETE` on disconnect, `SELECT COUNT(*)` for connected agent count, keyed lookups for routing. On node startup, the table is truncated (all connections are ephemeral — a server restart is a clean slate). This keeps on-prem to a single infrastructure dependency (Postgres) while still enabling HA. Cloud deployments (M10.2) can swap in Redis when scaling beyond 2 nodes.
- [x] **Single Space mode**: when only the Default Space exists, the UI hides the Space switcher entirely — feels like a single-tenant product.

---

### M10.2 — Cloud SaaS hardening

**Customer profile:** signs up at `krakendeploy.com`, gets a workspace at `acme.krakendeploy.com` (or `app.krakendeploy.com/s/acme`), pays subscription. Their agents in their infrastructure connect outbound to the central cloud server.

- [ ] **Object storage backends**: `IPackageStore` and `IArtifactStore` get S3 + Azure Blob + Cloudflare R2 implementations with per-Space prefixes (`s3://bucket/spaces/{spaceId}/packages/...`) and quotas. Local-FS impl stays for on-prem.
- [ ] **SignalR Redis backplane**: replace in-memory `IAgentConnectionRegistry` with Redis-backed for multi-replica server scale-out. Agent control-plane scales horizontally.
- [ ] **Per-Space rate limiting**: deployments per hour, package upload size, total storage, concurrent agents. Enforced in middleware; quota usage visible in Space settings.
- [ ] **Signup + trial lifecycle**: self-serve signup creates a Space + admin user + 14-day trial. States: Trial / Active / PastDue / Suspended / Deleted. Hangfire job handles transitions.
- [ ] **Stripe (or similar) billing**: subscription tiers (`Starter`, `Team`, `Business`, `Enterprise`); usage metering (extra targets / extra storage); webhook handler updates Space status.
- [ ] **Domain routing**: `app.krakendeploy.com/s/{spaceSlug}` baseline; optional CNAME → custom domain (Caddy on-demand TLS).
- [ ] **Blue/green / rolling deploys**: zero-downtime release process. EF migrations must be backwards-compatible across one version.
- [ ] **Status page integration**: Statuspage.io / Instatus webhook + in-app banner driven by current incidents.
- [ ] **Per-tenant observability**: Sentry tags by `space_id`, OTLP metrics labeled by `space_id`, per-Space audit log retention.
- [ ] **GDPR data export + delete**: Space admin can download all Space data as a tarball; "Delete Space" performs cryptographic erasure (drop encryption keys) + asynchronous data purge.
- [ ] **Anti-abuse**: suspicious-activity detection (mass deployment failures, rapid-fire signups from one IP, large package uploads from new accounts), automated holds with admin review queue.
- [ ] **Customer-side agent registration UX**: registration token encodes the target Space; agents bind to a Space they can never leave.
- [ ] **Caddy / Cloudflare in production**: Cloudflare in front for WAF + DDoS, Caddy on each replica for TLS to origin + gRPC fan-in.

### M10.3 — Octopus compatibility deepening + UX polish

Mid-M10 thread that didn't fit M5 (initial Octopus compat) or M9 (Kraken.IIS). Splits into Octopus-compat depth (system variables, output variables, step-execution surface) and UI polish (theme picker, dead buttons, layout consistency).

#### Done

- [x] **Theme picker** — `RadzenAppearanceToggle` (Material light/dark) in the header via a new `ThemeToggle` Razor component (own `@rendermode InteractiveServer` so it works inside SSR layouts). Persisted to `ApplicationUser.Theme` (new column + `AddUserTheme` migration); `UserService.UpdateThemeAsync`. `App.razor` switched from a static `<link>` tag to `<RadzenTheme>` so the choice drives the stylesheet at runtime.
- [x] **Dead-button + UX fixes** — Project Dashboard "Create Release" was inert (no Click handler), now opens `CreateReleaseDialog` and reloads on success. Global `/releases` page now actually loads data (new `ReleaseService.GetAllAsync()` overload) and shows the project name instead of the raw ProjectId guid. Tenants page was rendered SSR (button did nothing); added `@rendermode InteractiveServer`, made the Name column a `<RadzenLink>`, dropped the `RowClick` so clicking Delete no longer also navigates to the just-deleted tenant. Same render-mode fix on `TenantDetail`. Removed duplicate `<RadzenNotification />` from nine pages (the global `<RadzenComponents>` in `App.razor` already provides it; each local instance was rendering every toast twice).
- [x] **Layout consistency** — Right-aligned the create button via `JustifyContent.SpaceBetween` on Projects, Tenants, Environments, Lifecycles, Teams, Roles, Spaces, IdentityProviders, Dashboard. Swept all `Icon="add"` → `Icon="add_circle_outline"`.
- [x] **`Kraken.Script` step type + multi-language dispatcher** — new built-in step template `Kraken.Script — Run a Script` seeded by `BuiltInStepTemplateSeeder`. New `KrakenScriptConfigKeys` constants exposing the Octopus-compatible config-key names (`Octopus.Action.Script.ScriptBody`, `…Syntax`, `Octopus.Action.PowerShell.Edition`, `Octopus.Action.RunOnServer`). `ScriptRunner` now dispatches by syntax: PowerShell Core (`pwsh`) / PowerShell Desktop (`powershell.exe`, Windows-only with pwsh fallback) / Bash / CSharp (`dotnet script`) / FSharp (`dotnet fsi`) / Python. Script files get the right extension per syntax. `ScriptStepHandler` claims `Kraken.Script` alongside `Octopus.Script`, reads `Octopus.Action.PowerShell.Edition`, and only injects the PowerShell preamble when the syntax is PowerShell.
- [x] **Step-type unification** — retired legacy `KrakenDeploy.Script` step type and its lowercase `scriptBody` / `scriptSyntax` config keys. All UIs (`StepFormDialog`, `RunbookDetail`, `CreateStepTemplateDialog`) and the handler now use Octopus-compatible keys. Runbook scripts and offline-drop bundle generators (`DropBundleService`, `RunbookDetail`) previously used an ad-hoc `"ScriptBody"` key that didn't match what the handler reads — both now use `KrakenScriptConfigKeys.ScriptBody`, so runbook scripts and offline-drop orchestrators correctly pick up the script body.
- [x] **Octopus system variables (full list, server-side)** — new `OctopusSystemVariablesBuilder` emits ~70 `Octopus.*` system variables in 10 grouped sections (Deployment, Project, Release, Environment, Tenant, Machine/Tentacle, indexed per-step Action/Step, Web URLs, Time, deferred placeholders). Two entry points: `BuildForDeployment(...)` (wired into `DeploymentWorker` online + offline-drop paths) and `BuildForRunbookRun(...)` (wired into `RunbookRunWorker`). Variables without a Kraken-equivalent (Azure.\*, AWS.\*, Kubernetes.\*, created-by user fields, previous-successful queries, channel name lookups) are emitted as empty strings and flagged with `// TODO(kraken-equivalent)` comments for grep-audit. Agent-side: `ScriptStepHandler` injects the un-indexed current-step keys (`Octopus.Action.Name/Id/Number`, `Octopus.Step.Name/Number`, `Octopus.Action.Package.PackageId/PackageVersion/OriginalInstalledPath`) into both env vars and the PowerShell `$OctopusParameters` preamble.

#### Phase 6 — Octopus script-execution surface (remaining)

- [x] **6b: Artifact + output-variable stdout markers** — new `OctopusMessageParser` recognises `##octopus[setVariable]`, `##octopus[setOutputVariable]`, `##octopus[createArtifact]`, `##octopus[stdout-warning|error|default]`, and `##octopus[progress]` markers with base64 name/value decoding. Agent: `DeploymentExecutor` wraps the per-step `LogAsync` callback to intercept these — `setVariable` captures route to a per-step output-variable bucket and are suppressed from the user log, log-level markers set a sticky level for subsequent lines, artifact markers surface as info lines (the actual artifact files are still picked up by the existing artifacts-dir scan). PS preamble adds `Set-OctopusVariable` (emits the base64 `##octopus[setVariable]` marker) and `New-OctopusArtifact` (alias for `Register-KrakenArtifact`). Within a single deployment, prior-step output captures are merged into subsequent steps as `Octopus.Action[StepName].Output.X` keys via a `DeploymentPlan with { Variables = … }` augmentation — so step B's script can read step A's outputs through `$OctopusParameters` without any further round-trip.
- [x] **6c: Output variable persistence (server-side)** — new `DeploymentOutputVariable` entity + `deployment_output_variables` table (FK to `deployments`, unique index on `(DeploymentId, StepName, Name)` for upsert semantics), EF migration `AddDeploymentOutputVariables`. `IServerLink.ReportStepOutputVariablesAsync` + `IAgentHubServer.ReportStepOutputVariablesAsync` contract. SignalR implementation in `SignalRServerLink`; stubs in `DirectServerLink` and `PollingServerLink` (TODO: REST endpoints when those transports need server-side persistence). `AgentHub.ReportStepOutputVariablesAsync` upserts per `(DeploymentId, StepName, Name)`. *Not yet:* surfacing the captured outputs on the `DeploymentDetail` page — separate UI slice.
- [x] **6c-ui: surface captured outputs on `DeploymentDetail`** — "Output variables" tab added next to Log and Artifacts. Loads via new `DeploymentService.GetOutputVariablesAsync(deploymentId)`. Variables grouped by step in a card-per-step layout; each card shows Name / Value / Reference (the `#{Octopus.Action[StepName].Output.Name}` binding string for copy-paste) / Captured timestamp. Multi-line values render in a wrapping `<pre>`. Empty-state copy explains how scripts emit outputs.
- [x] **6d: Non-PowerShell variable injection** — `ScriptStepHandler` dispatches preamble by syntax: PowerShell (existing), Bash, Python, C# (dotnet-script), F# (dotnet fsi). Each language preamble exposes:
  - An ergonomic accessor (`OctopusParameters` dict in C# / F# / Python; `get_octopusvariable` helper in Bash since variable names with dots aren't valid bash identifiers).
  - `Set-OctopusVariable` / `set_octopusvariable` / `SetOctopusVariable` — emits the same base64 `##octopus[setVariable]` marker the agent parses, so output-variable capture works from any language.
  - `New-OctopusArtifact` / `new_octopusartifact` / `NewOctopusArtifact` — copies the file to `KRAKEN_ARTIFACTS_PATH` so the existing artifacts-dir scan picks it up.
- [x] **6e: `Server:BaseUrl` config** — new `Server.BaseUrl` setting in `appsettings.json` (empty default) and `appsettings.Development.json` (`https://localhost:5443`). `DeploymentWorker` and `RunbookRunWorker` read it from `IConfiguration` and pass it to `OctopusSystemVariablesBuilder.BuildForDeployment`/`BuildForRunbookRun`. When configured, `Octopus.Web.ServerUri`, `Octopus.Web.BaseUrl`, `Octopus.Web.DeploymentLink`, `Octopus.Web.ProjectLink`, and `Octopus.Web.ReleaseLink` resolve to real URLs; when blank, they stay empty (back-compat).

#### Phase 1–5 — Step-template library evolution

- [x] **1: Library data model extension** — `StepTemplate` gains `Source` (new `StepTemplateSource` enum: `UserAuthored` / `BuiltIn` / `CommunityLibrary` / `LocalImport`), `Category`, `Author`, `Website`, `LogoUrl`. EF migration `ExtendStepTemplate` adds the columns + indexes on `category` and `source`. `OctopusLibraryImporter.Parse` reads `Category` / `Author` / `Website` (also accepts `WebsiteUrl`) / `LogoUrl` (also accepts `Logo`) and populates the fields. `StepTemplateService.ImportFromJsonAsync` now takes a `StepTemplateSource` parameter (default `LocalImport`); community-catalog importer (Phase 4) will pass `CommunityLibrary`. `BuiltInStepTemplateSeeder` sets `Source = BuiltIn` plus initial `Category` (`iis`, `script`) and `Author = "KrakenDeploy"` on the two seeded templates.
- [x] **1b: Category taxonomy** — `category-mapping.json` embedded in `KrakenDeploy.Contracts.dll` (~80 small-bucket entries across 11 big buckets: Development and Scripting / Containers and Orchestration / Cloud Native Services / Infrastructure as Code / Server Environments / Configuration Management / Source Control / Notifications / Reporting and Telemetry / Security and Compliance / Workflow). New `StepTemplateCategoryMap` helper loads it once at startup; exposes `GetBigBucket(string?)`, `BigBuckets` (stable display order with `Other` last), and `All` (raw mapping for admin/debug surfaces). Unmapped categories fall into `Other`.
- [x] **2: Single-template import/export** — new `OctopusLibraryExporter.Serialize(template)` produces Octopus Community Library JSON that round-trips with `OctopusLibraryImporter.Parse`. New `GET /api/step-templates/{id}/export` endpoint returns the JSON as a file download named after the template. Download icon on every row of the Step Templates list. `ImportStepTemplateDialog` gains an `<InputFile>` file picker (alongside paste-JSON) — selecting a `.json` file loads its content into the textarea and records the filename as the import source.
- [x] **3: Bulk import from folder** — `StepTemplateService.ImportFromDirectoryAsync(folderPath)` recursively scans `*.json`, calls `ImportFromJsonAsync(..., source: LocalImport)` per file, classifies each as added/updated/skipped/errored. Returns `ImportFromDirectoryResult` (with per-file `ImportFromDirectoryError` list). New `POST /api/step-templates/import-folder` endpoint. New `ImportFolderDialog` Razor page with path input + summary card + per-file error grid; button on `/step-templates` opens it. Practical for pointing at a cloned `OctopusDeploy/Library/step-templates/` directory.
- [x] **4: Community catalog browser** — new `StepTemplateCatalogEntry` entity + `step_template_catalog` table (migration `AddStepTemplateCatalog`). `StepTemplateCatalogService` uses a single GitHub Git Trees API call to list all `step-templates/*.json` blobs with their per-file SHAs, then fetches only changed files via `raw.githubusercontent.com` (zero rate-limit cost). Upsert by `CommunityActionTemplateId`; orphan rows (deleted upstream) removed. Hangfire recurring job `kraken.step-template-catalog-poll` runs hourly (`Cron.Hourly()`). Named `HttpClient` `kraken.github` registered in DI with a `User-Agent` header (mandatory for GitHub API); optional `GitHub:Token` bumps 60-req/hr → 5000-req/hr. REST endpoints: `GET /api/step-template-catalog[?category=X]`, `POST /api/step-template-catalog/refresh`, `POST /api/step-template-catalog/{id}/install`. New `/step-templates/community` page mirrors the Octopus "Choose Step Template" screen: left filter pane with big-bucket category counts (driven by `StepTemplateCategoryMap`) + free-text search, right card grid with `LogoUrl`, author, description (4-line clamp), version + small-category badges, "More info" (opens `Website`) and "Install" buttons. Install fetches the full JSON and routes through `StepTemplateService.ImportFromJsonAsync(..., source: CommunityLibrary)`.
- [x] **5: Unified Add-Step dialog** — new `ChooseStepTemplateDialog` opens when a user clicks "Add Step" on a project's Process page. Layout mirrors the Octopus Choose-Step-Template screen: left filter pane with Featured / Installed / All + each big-bucket category from `StepTemplateCategoryMap`, plus a free-text search. Right card grid mixes (a) a permanent "Run a Script" sentinel, (b) every installed `StepTemplate` (built-in templates render as Featured), and (c) every uninstalled community catalog entry not already covered by an installed `CommunityActionTemplateId`. Action button per card is "Add Step" for installed and "Install and Add" for community — the latter routes through `StepTemplateCatalogService.InstallAsync` first. Return shape (`ChooseStepTemplateResult`) tells `Process.razor` to either: route the script sentinel + Script-flavoured templates through the existing `StepFormDialog`, or (for non-script templates) create the step directly with `ProcessSvc.AddStepAsync(..., template.ActionType, ..., config: template.Properties)`. A follow-up Phase 5b will add a parameter-driven form so non-script templates can be configured in the UI rather than via API.
- [x] **5b: Parameter-driven step form for non-script templates** — new `TemplatedStepFormDialog` renders one input per `StepTemplateParameter` based on its `ControlType` (`SingleLineText` → `RadzenTextBox`, `MultiLineText` → `RadzenTextArea`, `Sensitive` → `RadzenPassword`, `Checkbox` → `RadzenCheckBox<bool>` with `"true"`/`"false"` round-trip, `Select` → `RadzenDropDown` with `"value|Label"` options parsed at render time, `Package` falls back to `RadzenTextBox` until Phase 8 ships the package picker). Add path: pre-fills `Name` from template, populates each parameter with its `DefaultValue`. Edit path: pre-fills name/package/roles from the existing step plus each parameter value from `step.Config[paramName] ?? param.DefaultValue` (also preserves any pre-existing Config keys the template doesn't know about). Picker (Phase 5) now routes non-script templates straight into this dialog instead of creating the step immediately. `Process.razor.OpenEditStepAsync` switches on `step.StepType`: script → existing `StepFormDialog`, non-script → look up a `StepTemplate` by matching `ActionType` and open the new dialog (falls back to a warning notification when no template matches).

#### Phase 7 — Execution location

- [x] **7a: Execution-location UI** — `StepFormDialog` and `TemplatedStepFormDialog` gain a radio group ("Run on each deployment target" / "Run on the KrakenDeploy Server"). Persisted into step `Config` as `Octopus.Action.RunOnServer = "true"|"false"`. Edit path reads it back. The third Octopus mode (`Run on the server on behalf of each deployment target`) is documented in the help text as coming later — it needs per-target variable scoping on the server side.
- [x] **7b: Server-side script execution** — new `ServerScriptStepRunner` in `KrakenDeploy.Server.Transport` runs script steps in-process. Mirrors the agent's `ScriptRunner` (PowerShell Desktop/Core via `powershell.exe`/`pwsh`, Bash, CSharp `dotnet script`, FSharp `dotnet fsi`, Python; correct file extension per syntax). Writes log lines directly to `deployment_log_entries` and broadcasts via `UiHub` so the live-log surface is identical to the agent path. PowerShell preamble injects `$OctopusParameters` + Octopus-compat helpers (`Set-OctopusVariable`, `Write-Kraken*`, `Get-KrakenVariable`). `DeploymentWorker` now partitions plan steps by `RunOnServer`, runs server steps as a sync pre-phase, and dispatches only target steps to the agent. Fully-server-side deployments complete without involving an agent at all (works even when the target is offline). Ordering: server steps must precede target steps in declared order — interleaved ordering fails the deployment with a clear error. Mixed-order interleaving needs piecewise agent dispatch and is a separate piece of work.
- [x] **7c: "Server on behalf of each deployment target" mode** — in KrakenDeploy's one-target-per-deployment model, this collapses to a role filter on the server-side path. A server step with `TargetRoles` only runs when the deployment's target carries at least one matching role; a server step without roles always runs (pure "Run on Server" mode). Implemented as `StepAppliesToTarget(deployment, step)` in `DeploymentWorker`; UI help text on the radio explains how to use it (set Target Roles on a server step). Per-target iteration as Octopus does it isn't meaningful here because each KrakenDeploy `Deployment` already targets a single machine.
- [x] **7d: Interleaved server / target ordering** — dropped the "server steps must precede target steps" restriction. New `PartitionIntoGroups` splits the plan into consecutive same-side groups; `DeploymentWorker` walks them in declared order. For each target group it dispatches a sub-plan to the agent and awaits completion via a new `IPendingSubPlanRegistry` (singleton coordinator backed by `TaskCompletionSource`-per-deployment). `AgentHub.CompleteDeploymentAsync` checks the registry first: when a TCS is pending, the hub resolves it and returns instead of finalizing the deployment, so the worker resumes. After all groups complete, the worker finalizes the deployment as `Succeeded`. A failed sub-plan or a failed server step short-circuits the loop and fails the deployment with the underlying error. `DeploymentStepPlan` also gains `TargetRoles` (backward-compatible nullable append) so the server-side runner has the role list it needs without re-querying the snapshot.

#### Phase 8 — Referenced packages

- [x] **8a: Step config schema** — new `PackageReference` record in `KrakenDeploy.Contracts/Steps/` (Name / PackageId / Version / Extract / FeedId). Stored on the step as a JSON-encoded array under `Octopus.Action.Package.PackageReferences` (the Octopus-compatible key, exported as a constant on `KrakenScriptConfigKeys`). Resolved at plan-build time by the new `PackageReferenceResolver`: entries without an explicit `Version` get the latest uploaded version of the named `PackageId`. Pinning at release-creation time (channel rules etc.) is carved out as 8e.
- [x] **8b: Agent extraction** — `DeploymentStepPlan` gains a nullable `ReferencedPackages` list (backward-compatible append). `DeploymentExecutor.ExecuteStepAsync` downloads each via `GrpcPackageDownloader`; if `Extract = true` (default) the zip is unpacked to `{tempRoot}/extracted/refs/<sanitised-name>/` (or `{tempRoot}/refs/...` for steps without a primary package). Paths are collected into `StepHandlerContext.ReferencedPackagePaths`. Failure to fetch or extract any referenced package fails the step.
- [x] **8c: Variable + env injection** — `ScriptStepHandler` exposes each referenced package via `Octopus.Action.Package[<Name>].ExtractedPath` (set in `$OctopusParameters` via the preamble AND as an env var) plus `OCTOPUS_REFERENCED_PACKAGE_<NAME>_PATH` (Octopus naming convention). Scripts can use either accessor.
- [x] **8d: UI** — `StepFormDialog` (script form) gains a "Referenced Packages" section with an inline grid (Name / Package ID / Version — blank = latest) plus per-row delete and a top-level "Add" button. Persists/loads the JSON; empty rows are dropped at save.
- [x] **8e: Release-time version pinning** — `ReleaseService.CreateAsync` now calls `PinReferencedPackagesAsync` per step when building the `ProcessSnapshot`. Entries with an explicit `Version` pass through; entries without one resolve to the latest uploaded version of the named `PackageId` via the same strict `ResolveLatestVersionAsync` used for the primary package (throws if the package has zero uploaded versions). The resolved list is re-serialised back into the snapshotted Config under `Octopus.Action.Package.PackageReferences`, so every deploy of the release runs with the exact same set of referenced packages. `PackageReferenceResolver` at deploy time now sees pre-pinned data and just passes through. Steps without a primary `PackageId` no longer require one (previous code always called `ResolveLatestVersionAsync`); useful for fully-server-side script steps that don't ship a package.

#### Built-in step pack (Octopus parity)

The Octopus "built-in" step pack splits into two distinct classes that need different work:

**(A) `Octopus.*Script`-based ActionTemplates exposed by `/api/actiontemplates`.** These are templates the Octopus installer / admin can install on top of `Octopus.Script` — File System, IIS AppPool helpers, Windows Service - Check status, Upload files by FTP, etc. They have a `CommunityActionTemplateId` and their `Properties` carry a script body + parameters. Importing them is purely metadata work — no new handlers needed, because they all run on the existing `Kraken.Script` / `Octopus.Script` handler.

- [x] **Octopus API dump importer** — `StepTemplateService.ImportFromOctopusApiResponseAsync(json)` unwraps the paginated `{ItemType:"ActionTemplate",Items:[…]}` shape returned by `GET /api/actiontemplates` and routes each item through the existing `ImportFromJsonAsync(..., source: LocalImport)`. Endpoint `POST /api/step-templates/import-octopus-api`. UI: "Import Octopus dump" button + dialog on `/step-templates` accepts paste or file picker.

**(B) True built-in ActionTypes baked into `Octopus.Server`'s binaries** (do NOT appear in `/api/actiontemplates`). Each needs its own Kraken-native handler. Sourced from public Octopus docs + observable behaviour — **not** decompiled from Calamari (see [docs/architecture.md](docs/architecture.md#step-execution-model) on the clean-room policy).

Real Argosy + WebArgosy `deploymentprocess` exports drive the priority order: 49 × `Octopus.TentaclePackage` (all three of `CustomDirectory` / `ConfigurationVariables` / `ConfigurationTransforms` features in use), 2 × `Octopus.IIS` (full website + bindings + app pool + `CustomDirectory` + `CustomScripts`), 1 × `Octopus.DeployRelease`.

Strategy is **dual-shape**: the importer preserves the Octopus property bag verbatim in `DeploymentStep.Config` (the jsonb `Dictionary<string,string>` already exists — no schema change); handlers add an `Octopus.*` parser alongside the existing `Kraken.*` parser, detect shape by key prefix, and dispatch internally. Feeds aren't modelled — `Octopus.Action.Package.FeedId` is preserved as an opaque string until a `Feed` aggregate is justified. `EnabledFeatures` stays as a verbatim comma-separated string in `Config["Octopus.Action.EnabledFeatures"]`; handlers split-parse at runtime.

##### Phase B-1 — `Octopus.TentaclePackage` handler

- [x] **B-1: `OctopusTentaclePackageStepHandler`** — new handler in `KrakenDeploy.Agent/Deployment/Package/`. Claims `Octopus.TentaclePackage`. `RequiresPackage = true`. Reads `Octopus.Action.Package.*` keys + parses `Octopus.Action.EnabledFeatures`. Orchestrates the per-feature passes against `context.ExtractDir`:
  - `Octopus.Features.CustomDirectory` — copy contents of `ExtractDir` to `Octopus.Action.Package.CustomInstallationDirectory` (Octostache-substituted). When `…ShouldBePurgedBeforeDeployment="True"`, purge the destination first, honoring `…CustomInstallationDirectoryPurgeExclusions` (newline- or comma-separated glob list, e.g. `App_Data`).
  - `Octopus.Features.ConfigurationVariables` + `…AutomaticallyUpdateAppSettingsAndConnectionStrings="True"` — XML `appSettings` and `connectionStrings` substitution against deployment variables: for each `*.config` file, replace `<add key="X" value="…" />` and `<add name="X" connectionString="…" />` whose key/name matches a deployment variable. **Not** raw Octostache placeholder substitution (that's the separate `Octopus.Features.SubstituteVariablesInFiles` feature handled by the existing `SubstituteVariablesStepHandler`).
  - `Octopus.Features.ConfigurationTransforms` + `…AutomaticallyRunConfigurationTransformationFiles="True"` — apply XDT transforms (`*.<env>.config` over `*.config`) via `Microsoft.Web.Xdt` 3.2.8. Auto-discovery matches the deployment's `EnvironmentName` against sibling transform files; explicit `Octopus.Action.Package.AdditionalXmlConfigurationTransforms` mappings are a follow-up.
  - DI registration in `Agent/Program.cs`.
  - Unit tests in `KrakenDeploy.Agent.Tests`: each feature in isolation, all three combined, purge-with-exclusions edge cases, ConfigurationVariables matching `appSettings` and `connectionStrings`, XDT transform applied for `Octopus.Environment.Name`.

##### Phase B-2 — Octopus `deploymentprocess` JSON importer

- [x] **B-2: `DeploymentProcessImportService`** — accepts an Octopus `GET /api/{spaceId}/deploymentprocesses/{processId}` JSON. Maps each `Steps[].Actions[0]` → Kraken `DeploymentStep`. Field mapping (no key translation in `Config`):
  - `Action.ActionType` → `StepType` (verbatim, e.g. `Octopus.TentaclePackage`).
  - `Action.Properties` → `Config` (verbatim, all `Octopus.Action.*` keys preserved).
  - `Step.Properties["Octopus.Action.TargetRoles"]` (comma-separated) → `TargetRoles: List<string>`.
  - `Action.Packages[0].PackageId` → `DeploymentStep.PackageId` (when present and not the `"dummy"` placeholder).
  - `Step.Name`, `Step.Slug`, `Action.IsDisabled`, `Action.Notes` mapped to existing Kraken columns.
  - `Action.TenantTags[]` mapped to Kraken tenant tags by tag-set/tag-name lookup (skip with a warning when the tag doesn't exist locally).
  - `WorkerPoolId`, `Container`, `Channels`, `Environments`/`ExcludedEnvironments` — out of LAUS scope; logged as ignored.
  - REST endpoint: `POST /api/projects/{id}/deployment-process/import-octopus`.
  - UI: dialog on the project Process page modelled on `ImportOctopusApiDialog` (paste-textarea + file picker, summary card, per-step error grid).
  - Tests: import both supplied real exports (`argosy-process.json` 55 steps, `webargosys2s-2-process.json` 22 steps), assert step counts, types, and that `Octopus.Action.IISWebSite.Bindings` round-trips byte-for-byte.

##### Phase B-3 — `Octopus.IIS` dual-shape support

- [x] **B-3a: `OctopusIisConfig.Parse`** — new parser alongside the existing `KrakenIisConfig.Parse` in the IIS handler. Shape detection: presence of `Octopus.Action.IISWebSite.WebSiteName` or `…VirtualDirectory.CreateOrUpdate` or `…WebApplication.CreateOrUpdate` → Octopus shape; otherwise → Kraken shape. Reads:
  - `Octopus.Action.IISWebSite.DeploymentType` ∈ `{webSite, webApplication, virtualDirectory}` (discriminator).
  - **webSite branch:** `WebSiteName`, `Bindings` (JSON-in-string — Octostache-substitute the string first, then `JsonSerializer.Deserialize`, then walk each `{protocol, ipAddress, port, host, thumbprint, certificateVariable, requireSni, enabled}`; `requireSni` and `enabled` can each be raw `true`/`false` OR Octostache-evaluated strings), `ApplicationPoolName`, `ApplicationPoolFrameworkVersion` (`v2.0`/`v4.0`/`No Managed Code`), `ApplicationPoolIdentityType` (`ApplicationPoolIdentity`/`LocalSystem`/`LocalService`/`NetworkService`/`SpecificUser`), `ApplicationPoolUsername`/`ApplicationPoolPassword` (only when identity is `SpecificUser`), `EnableAnonymousAuthentication`/`EnableBasicAuthentication`/`EnableWindowsAuthentication`, `WebRootType` (`packageRoot`/`packageDirectory`), `StartWebSite`/`StartApplicationPool`, `CreateOrUpdateWebSite`.
  - **webApplication branch:** `WebApplication.WebSiteName`, `WebApplication.VirtualPath`, `WebApplication.ApplicationPoolName`, `WebApplication.ApplicationPoolFrameworkVersion`, `WebApplication.ApplicationPoolIdentityType`, `WebApplication.CreateOrUpdate`.
  - **virtualDirectory branch:** `VirtualDirectory.CreateOrUpdate` + shared keys.
  - Shared: `Octopus.Action.Package.*` payload keys (delegated to the B-1 package machinery).
- [x] **B-3b: Map → `KrakenIisConfig`** — translate parsed Octopus shape into the existing `KrakenIisConfig` so `IisScriptGenerator.Generate` stays the single code path. This keeps the script-emit + artifact-write + run flow identical for both shapes.
- [x] **B-3c: Dummy-package quirk** — when `Action.Packages[0].PackageId == "dummy"` and `Octopus.Action.IISWebSite.WebRootType == "packageRoot"`, no extraction is attempted (the step only configures IIS). The B-2 importer flags this case during mapping; the handler is told via a config sentinel rather than inferring it.
- [x] **B-3d: Tests** — fixtures for each `DeploymentType` branch, the WebArgosy `webSite` real export, bindings with Octostache-conditional `enabled`, SpecificUser app-pool identity, dummy-package round-trip.

##### Phase B-3 follow-ups (not in B-3 scope)

- [x] **`Octopus.IIS` webApplication branch** — `KrakenIisWebApplicationConfig` (Contracts) + `IisScriptGenerator.GenerateWebApplication`; `OctopusIisConfig.MapWebApplication` reads `Octopus.Action.IISWebSite.WebApplication.*` (parent site, virtual path, own app pool) + `Octopus.Action.Package.CustomInstallationDirectory` for physical path; handler dispatches via `MappingResult.WebApplication`.
- [x] **`Octopus.IIS` virtualDirectory branch** — `KrakenIisVirtualDirectoryConfig` (Contracts) + `IisScriptGenerator.GenerateVirtualDirectory`; `OctopusIisConfig.MapVirtualDirectory` reads `Octopus.Action.IISWebSite.VirtualDirectory.{WebSiteName,VirtualPath,CreateOrUpdate}`; no app pool (inherited from parent).
- [ ] **Auth toggle support in `KrakenIisConfig`** — extend the strongly-typed config to honour `EnableAnonymousAuthentication` / `EnableBasicAuthentication` / `EnableWindowsAuthentication`. Mapper currently warns and falls through to IIS defaults.

##### Already covered / deferred

- [x] **`Octopus.DeployRelease`** — new `DeployReleaseStepRunner` in `KrakenDeploy.Server.Transport`. Reads `Octopus.Action.DeployRelease.{ProjectId,DeploymentCondition}` (Octostache-evaluated). Resolves the child project by Guid -> Slug -> Name (case-insensitive via Postgres `ILIKE`); picks the project's latest release; evaluates the condition (`Always` / `IfNewer` / `IfNotCurrent`; `IfChannelHasChanged` warns and falls back to `Always`). Triggers the child via `DeploymentService.CreateAsync` against the parent's environment + target + tenant. New `Deployment.ParentDeploymentId` column + `AddDeploymentParentLink` EF migration tracks the link. Runner polls the child's `DeploymentLogEntry` rows at 500 ms cadence and mirrors each new line into the parent's log prefixed with `[<stepName> -> <childId>]`. Returns true when the child reaches `Succeeded`. `DeploymentWorker.IsServerStep` now recognises `Octopus.DeployRelease` intrinsically (no `Octopus.Action.RunOnServer=true` flag needed) via a `ServerOnlyStepTypes` set; worker's server-step dispatch routes by `StepType` to the appropriate runner.
- [x] **`Octopus.Manual`** — `ManualInterventionStepHandler` aligned with the Octopus public contract. Reads `Octopus.Action.Manual.Instructions` (Octostache-evaluated), `Octopus.Action.Manual.ResponsibleTeamIds` (logged for audit), `Octopus.Action.Manual.BlockConcurrentDeployments` (logged + ignored — Kraken runs unattended). Legacy un-prefixed `Instructions` key honoured for back-compat. New `OctopusManualConfigKeys` constants alongside the handler.
- [ ] **`Octopus.AwsRunCloudFormation`, `Octopus.AzureFunction`** etc. — long tail; transcribe as Argosy/WebArgosy processes need them. Azure / AWS / Kubernetes packs deferred.

### M10.4 — Schema-driven step UI + step-package plugin system

Two coupled architectural changes that take Kraken from "step handlers compiled into the agent" to "step types as standalone, versioned, dynamically-loaded packages". Modelled on Octopus's [Step Package framework](https://octopus.com/blog/improving-delivery-deployment-steps) but with the Node.js runtime swapped out for .NET — `AssemblyLoadContext`-based plugin loading instead of `node` subprocesses, so authors stay in C# and target Windows Server / Linux equally. Adopts the declarative-UI *idea* (schemas instead of hand-coded editors) without paying for the foreign toolchain.

Phase C and Phase D are sequential: Phase C nails the schema language used by step editors and is a prerequisite for Phase D-1 (every package carries a schema). Phase D adds the loader, per-step versioning, the GitHub feed, and refactors the existing built-ins into packages.

**Handler lifecycle decision (locked):** `IStepHandler` instances are constructed fresh per step execution and disposed after `HandleAsync` returns. No long-lived per-deployment handler state. Handlers needing shared state (e.g., an `HttpClient` pool) use a `static` in their own package.

**ABI window:** because no installations exist yet, `KrakenDeploy.Contracts` may be refactored without backward-compatibility constraints up to Phase D-2. Once D-2 publishes `Kraken.SDK` NuGet, the surface freezes — additive-only after that.

#### Phase C — Schema-driven step UI

Octopus's framework exposes a declarative TypeScript DSL that compiles to a JS bundle and is lazy-loaded by their portal. For Kraken we keep the *idea* (a renderer-agnostic schema describing one step's editor) but use a JSON Schema subset extended with Kraken widget annotations, authorable as either a C# attribute graph on a POCO **or** an embedded JSON resource. One Razor renderer handles every step type — retiring the hand-coded `StepFormDialog`, `TemplatedStepFormDialog`, and any future per-type editor. The same schema drives a hypothetical future MAUI editor without changes.

- [x] **C-1: Step-UI schema IR** — define the schema language in `KrakenDeploy.Contracts.Steps.StepUiSchema`. Subset of JSON Schema (draft 2020-12) plus Kraken widget annotations. Supported field types: `string`, `number`, `integer`, `boolean`, `object`, `array`. Per-field annotations: `widget` (one of `text|textarea|sensitive|select|number-input|checkbox|variable-ref|certificate-ref|package-ref|target-roles|json-editor|file-picker`), `label`, `helpText`, `placeholder`, `default`, `enumValues` (label + value pairs for `select`), `visibleWhen` (JsonPath-style predicate over sibling field values for conditional show/hide), `validation` (`required`, `minLength`, `maxLength`, `pattern`, `min`, `max`). For arrays: `itemSchema` (recurses); for objects: `properties`. Schema root: `id`, `title`, `description`, `version` (matches the step package version), `groups` (named groups of properties so the renderer can show collapsible sections — e.g. "Web Site" / "App Pool" / "Bindings" / "Health Check" for `Kraken.IIS`).

- [x] **C-2: Schema authoring API** — two equivalent paths in `KrakenDeploy.Contracts.Steps`. (a) **C# attributes** on a POCO: `[StepUiField(Widget = "text", Label = "Site name", Group = "WebSite")] public string SiteName { get; set; }` — reflection-based emitter walks the POCO and produces a `StepUiSchema`. (b) **Embedded JSON**: package authors can ship `ui-schema.json` inside the step package zip and skip attributes entirely. Both paths produce the same runtime `StepUiSchema` object so the renderer doesn't care. Static `StepUiSchemaBuilder` exposes `FromType(Type t)` and `FromJson(string json)`. Round-trippable: emit a schema POCO → produce JSON; parse the JSON → equivalent schema object.

- [x] **C-3: Schema validator + value coercion** — `StepUiSchemaValidator.Validate(StepUiSchema schema, Dictionary<string,string> values)` returns a list of per-field validation errors. Used server-side at save time (reject invalid `DeploymentStep.Config` before persisting) and client-side at form-edit time (live validation). Also `StepUiSchemaValidator.CoerceFromConfig(StepUiSchema, Dictionary<string,string>)` — translates the flat `string→string` Config bag into typed JSON form values for the renderer (booleans, numbers, arrays serialised as JSON in the bag). Inverse `CoerceToConfig(StepUiSchema, JsonObject formValues)` converts the renderer's typed output back into the storage shape.

- [x] **C-4: `SchemaDrivenStepFormDialog` Razor renderer** — new component in `KrakenDeploy.Server/Components/Dialogs/` taking `StepUiSchema schema` and `Dictionary<string,string> initialValues` and emitting an editable form. Renders one Radzen input per field by widget type. Honours `visibleWhen` predicates (re-renders affected fields on change). Honours `groups` (collapsible `RadzenCard` per group). Wires `validation` annotations to `RadzenFieldset` validators. Returns the mutated `Dictionary<string,string>` on Save. Two modes: **inline** (rendered inside a wider step-creation flow next to the step picker) and **dialog** (full-screen modal for editing existing steps). Widget renderers behind `IStepUiWidget` so future MAUI / desktop renderers reuse the same widget catalogue.

- [x] **C-5: Author schemas for existing step types** — write the schema for each currently-hardcoded step type and retire the hand-written editor:
  - `Kraken.IIS` — groups: General / App Pool / Recycling / Rapid-Fail / Bindings (array) / Application Init / Deploy Strategy / Health Probe. Bindings array uses an inner schema (per-row `protocol`/`ip`/`port`/`host`/`thumbprint`/`store`/`sniRequired`/`sslFlags`). Conditional visibility: App-pool Username/Password visible only when `IdentityType=SpecificUser`; HealthCheck fields visible only when `HealthCheck.Url` is set.
  - `Octopus.IIS` (Octopus-shape) — groups: Web Site / Web Application (visible only when `DeploymentType=webApplication`) / Virtual Directory (visible only when `DeploymentType=virtualDirectory`) / App Pool / Authentication / Bindings / Features. Bindings field uses the raw `json-editor` widget (Octopus's JSON-in-string format) — no UI decomposition, since the user-facing edit story is "imported from Octopus, round-trips back".
  - `Octopus.TentaclePackage` — Custom Directory (visible when `Octopus.Features.CustomDirectory` enabled), Configuration Variables (visible when feature enabled), Configuration Transforms (visible when feature enabled), Substitute Variables in Files (visible when feature enabled).
  - `Kraken.Script` / `Octopus.Script` — Script body (`textarea`), Syntax (`select` with PowerShell/Bash/CSharp/FSharp/Python), PowerShell Edition (`select` visible only when `Syntax=PowerShell`), Run on (`select` agent/server).
  - `Octopus.SubstituteVariables` — single `textarea` for target-file globs.
  - `Octopus.JsonConfigurationVariables` — single `textarea` for JSON config-variable targets. (Was historically named `Octopus.FileTransform`; renamed in D-8.3 to match Octopus's own docs — XDT for XML lives on `Octopus.TentaclePackage`, not here.)
  - `Octopus.Manual` — Instructions (`textarea`), Responsible team (`target-roles` widget).

- [x] **C-6: Bridge for legacy `StepTemplateParameter`** — `StepTemplateSchemaAdapter` in `KrakenDeploy.Server.Data.Services` builds a `StepUiSchema` from a `StepTemplate` or directly from a parameter list (`BuildSchema(StepTemplate)` + `BuildPropertyMap(IReadOnlyList<StepTemplateParameter>)`). The bridge lives in `Server.Data` because it crosses the `KrakenDeploy.Server.Core` (`StepTemplateParameter`) → `KrakenDeploy.Contracts` (`StepUiSchema`) domain boundary that Contracts can't see. `ControlType` mapping: `SingleLineText` → `text`; `MultiLineText` → `textarea`; `Sensitive` → `sensitive`; `Checkbox` → `checkbox`; `Select` → `select` (with `value|label` parsing on `SelectOptions`); `Package` → `package-ref`; unknown → defensive `text` fallback. `Checkbox` yields `StepUiFieldType.Boolean`, everything else `String`. Legacy parameters never carry validation / visibleWhen / group metadata. **Dialog unification shipped:** the old `TemplatedStepFormDialog` was deleted and the old script-only `StepFormDialog` was replaced with a unified `StepFormDialog` that resolves any step type's schema (`BuiltInStepSchemas` first → `StepTemplateSchemaAdapter.BuildSchema(template)` fallback). A pure-body `StepUiSchemaForm` Razor component is shared by both `SchemaDrivenStepFormDialog` and the unified `StepFormDialog`. One renderer for every step type; `Process.razor` dispatches all Add and Edit flows through the single dialog.

#### Phase D — Step package plugin system

Steps ship as standalone signed `.kdeploy-step` packages instead of being compiled into the agent binary. Each package contains its declarative UI schema (from Phase C), its C# executor DLL (an `IStepHandler` implementation), and a manifest. Server stores multiple versions side-by-side. Agent downloads on demand at deploy time and loads via an isolated `AssemblyLoadContext`. Each `DeploymentStep` pins an exact step-package version; the snapshot pins it again at release-creation time for repeatability. New versions surface via a GitHub-hosted feed as "update available" badges but never auto-apply.

- [ ] **D-1: Package format + Contracts ABI lockdown** — define the `.kdeploy-step` zip layout: root `manifest.json`, `executor/` directory (executor DLL + direct deps), `ui/ui-schema.json` (Phase C schema), optional `README.md`, optional `logo.png`, optional `changelog.md`. Manifest schema: `id` (e.g. `kraken.iis`), `version` (semver), `displayName`, `description`, `author`, `targetFramework` (`net10.0`), `stepTypes` (array of step-type strings the executor claims via `IStepHandler.CanHandle`), `minKrakenAgent` (semver lower bound), `executorAssembly` (filename inside `executor/`), `executorTypeName` (fully-qualified class name implementing `IStepHandler`), `signature` (base64 RSA-SHA256 over the canonical manifest+executor-DLL hash), `signedBy` (key identifier — for v1 always `"kraken-project"`). Free-rein refactor of `KrakenDeploy.Contracts`: stable plugin surface is `IStepHandler`, `StepHandlerContext`, `DeploymentPlan`, `DeploymentStepPlan`, `PackageReference`, all relevant `KrakenIisConfig` types, and the Phase C `StepUiSchema` types. Mark non-surface internals with `[EditorBrowsable(Never)]`. Document the ABI surface in `docs/sdk-surface.md`.

- [x] **D-2: `Kraken.SDK` NuGet + `dotnet new krakenstep` template** —
  - **Kraken.SDK NuGet** (done): `KrakenDeploy.Contracts.csproj` sets `IsPackable=true` + `PackageId=Kraken.SDK` + version / author / description / license metadata. `dotnet pack -c Release` produces `Kraken.SDK.{Version}.nupkg`. NoWarn suppresses doc-comment noise on protobuf-generated stubs (CS1591) and record-positional-param doc placement (CS1574/CS1587/CS1573) that's valid C# but doesn't satisfy the XML-doc tooling. Internal name stays `KrakenDeploy.Contracts` so the agent + server + Steps.* projects don't churn their ProjectReferences.
  - **Kraken.SDK.Templates package** (done): new `templates/KrakenDeploy.Templates/` project produces `Kraken.SDK.Templates.{Version}.nupkg` carrying the templated `KrakenStep/` project content. `.template.config/template.json` registers the template under short name `krakenstep` with three parameters (`stepTypeId`, `packageId`, `sdkVersion`) and `sourceName: "KrakenStep"` so the project + namespaces rename to the user's `-n` value. Scaffolded layout:
    - `MyStep.csproj` — `<PackageReference Include="Kraken.SDK" />` + a self-contained inline `<Target Name="PackKrakenStepPackage">` that produces `{packageId}-1.0.0.kdeploy-step` on `dotnet build` (same lay-out logic as `steps/KrakenStepPackage.targets`; inlined so the scaffolded project builds standalone without needing build assets from the NuGet).
    - `SampleStepHandler.cs` — `IStepHandler` skeleton with documented lifecycle, `CanHandle` matching the user's `stepTypeId`, sample `HandleAsync` body, comments pointing at `context.Plan/Step/LogAsync/ArtifactsDir`.
    - `ui-schema.json` — minimal one-property example.
    - `README.md` — pointers at next steps + the SDK doc.
  - **End-to-end verified**: `dotnet new install Kraken.SDK.Templates.1.0.0.nupkg` → `dotnet new krakenstep -n MyTestStep --stepTypeId "Acme.DeployToS3" --packageId "acme.deploy-s3"` → `dotnet build --source <local-feed>` → `acme.deploy-s3-1.0.0.kdeploy-step` archive with the right `manifest.json` + `executor/MyTestStep.dll` + `ui/ui-schema.json` layout. Placeholder substitution (`KrakenStep` → `MyTestStep`, `STEP_TYPE_PLACEHOLDER` → `Acme.DeployToS3`, `PACKAGE_ID_PLACEHOLDER` → `acme.deploy-s3`) works across .cs / .csproj / .json files.
  - **Deferred**: the `kraken pack` CLI verb. The current MSBuild pack-on-build covers the same ground without an extra CLI surface; the CLI verb makes more sense as part of D-12 once signing key management exists (it adds `--key ./signing.key` on top of the build).
  - 553 total tests still pass.

- [ ] **D-3: Server multi-version storage + manual upload** — new `StepPackage` aggregate in `KrakenDeploy.Server.Core.Domain.StepPackages` with fields `Id`, `Name` (manifest `id`), `Version` (semver), `Sha256`, `Manifest` (jsonb), `UiSchema` (jsonb), `InstalledUtc`, `Source` (enum `LocalUpload`/`CatalogPull`). EF migration `AddStepPackages` adds `step_packages` table with unique index on `(name, version)`. Disk layout: `{dataPath}/step-packages/{name}/{version}/` containing the extracted contents plus the original signed zip alongside. New endpoints in `KrakenDeploy.Server`:
  - `POST /api/step-packages` — multipart upload of a `.kdeploy-step` zip. Validates manifest against schema, verifies signature against project public key (compiled-in constant; configurable for dev via `appsettings.Development.json` allowlist), computes SHA-256, checks `(name, version)` uniqueness (HTTP 409 on dup), extracts to disk, persists the row, returns the created record.
  - `GET /api/step-packages?name=X` — all installed versions of a named package.
  - `GET /api/step-packages/{id}` — single package detail with full manifest + schema.
  - All gated by a new `Permission.StepPackageManage`.
  - New Razor page `/step-packages` lists installed packages grouped by name, version-history descending, with upload dialog + per-version uninstall button (D-11).

- [x] **D-4: Agent loader + `AssemblyLoadContext` isolation** — `StepPackageLoader` in `KrakenDeploy.Agent.StepPackages` resolves a `(name, version)` package from `{dataPath}/step-packages-cache/{name}/{version}/`, verifies the signature placeholder (gated by `StepPackages:AllowUnsignedLoads`), and loads the executor into a collectible `StepPackageAssemblyLoadContext`. The ALC `Load` override delegates any assembly already in the default ALC back to it — Contracts, `System.*`, `Microsoft.Extensions.*` all flow through this branch so plug-in `IStepHandler` types share identity with the agent's view. `CreateHandler(name, version)` returns a fresh instance per call (per-step-execution lifecycle). Covered by 11 tests in `StepPackageLoaderTests` including the type-identity assertion.

- [x] **D-5: gRPC channel for step-package delivery** —
  - `kraken.proto` extended with `service StepPackageDelivery { rpc DownloadStepPackage(StepPackageDownloadRequest) returns (stream StepPackageChunk); }`. `StepPackageChunk` carries `data`, `total_bytes` (first chunk only), `is_last`, and a `sha256` trailer string (populated only on the last chunk).
  - Server: `GrpcStepPackageDeliveryService` streams the on-disk archive at `{dataPath}/step-packages/{name}/{version}/package.kdeploy-step` in 64 KB chunks, hashing incrementally with `SHA256.TransformBlock` and emitting the digest on the trailer chunk. Authorized via the same `AgentJwt` scheme as `GrpcPackageDeliveryService`; mapped in `Program.cs` alongside the existing gRPC services. No delta / no resume — packages are small.
  - Agent: `GrpcStepPackageDownloader` in `KrakenDeploy.Agent.Transport` implements `IStepPackageSource`. Streams chunks to a temp file, hashes incrementally, compares against the trailer digest, then invokes a configurable `extract` callback (wired to `StepPackageLoader.ExtractToCache` in production). Constructor accepts `Func<string>` accessor delegates for `serverUrl` + `agentToken` so the singleton can be registered before `AgentContext` is ready. Channel rotates if the URL changes.
  - Loader integration: `StepPackageLoader` takes an optional `IStepPackageSource`. New `TryLoadOrDownloadAsync(name, version, ct)` consults the cache, downloads on miss, retries the load.
  - DI: `Program.cs` registers `StepPackageLoader` + `GrpcStepPackageDownloader` as singletons; the downloader's extract callback uses a deferred service-locator lookup so the loader→source→loader graph stays acyclic.
  - Tests: 4 stream-handling unit tests in `GrpcStepPackageDownloaderTests` (happy path, missing trailer, SHA mismatch, empty payload) drive the internal `DownloadAndExtractAsync(IAsyncEnumerable<StepPackageChunk>)` helper directly — no live gRPC server needed. 3 loader-integration tests in `StepPackageLoaderDownloadTests` cover the missing-source, source-throws, and pull-then-reload paths.

- [x] **D-6: Per-step version pinning + snapshot pinning** —
  - The pin is `(StepPackageName, StepPackageVersion)` — name + version travel together because step type and package name differ (e.g. step `Octopus.IIS` lives in package `octopus.iis`), and the loader's cache key is `(name, version)`. Both columns nullable for the D-6 → D-8 transition; when both null the agent falls back to its hardcoded handler.
  - **Storage**: nullable `step_package_name varchar(128)` + `step_package_version varchar(64)` columns on `deployment_steps` AND `runbook_steps`. Migration `AddStepPackageVersionColumns`. JSONB `process_snapshot` picks up the new properties on read via System.Text.Json — no schema change needed for snapshots.
  - **Resolver**: new `StepPackageResolver` in `KrakenDeploy.Server.Data.Services`. `ResolveLatestForStepTypeAsync(stepType, ct)` returns `StepPackagePin?` (Name, Version) by scanning `step_packages.step_types` with case-insensitive ILIKE on the comma-joined list. Picks the highest version via an internal semver comparator (MAJOR.MINOR.PATCH + pre-release suffix; mirrors SemVer 2.0.0 closely enough for "latest installed" without pulling NuGet.Versioning).
  - **Services**: `ProcessService.AddStepAsync` / `UpdateStepAsync` accept optional `(stepPackageName, stepPackageVersion)`; when both null, ProcessService auto-resolves via the resolver. `ProcessService.ImportDeploymentProcessAsync` also auto-resolves on each imported step. `RunbookService.AddStepAsync` / `UpdateStepAsync` mirror the same shape on the runbook entity. `ReleaseService.CreateAsync` copies the pin from the live step into the snapshot; if the live step had no pin (older row, or no package installed at the time), re-resolves "latest installed" *now* so the release is reproducible.
  - **Wire contract**: `DeploymentStepPlan` gains nullable `StepPackageName` + `StepPackageVersion` (appended for back-compat). `DeploymentWorker` + `RunbookRunWorker` forward both from the snapshot.
  - **Agent executor**: `DeploymentExecutor` consults `StepPackageLoader.TryLoadOrDownloadAsync(name, version)` whenever the plan carries a pin. The package's `IStepHandler` is instantiated per step execution (Activator) and takes priority over any in-DI handler. Falls back to the in-DI handler list when the pin is absent OR when the loader can't produce a working handler (D-8 retires the fallback once every built-in is package-backed).
  - **REST**: `AddStepRequest` accepts optional `StepPackageName` + `StepPackageVersion`; project/process endpoints + runbook endpoints forward both.
  - **Tests**: 9 unit tests for the semver comparator + step-type lookup + multi-step-type packages (`StepPackageResolverTests`). 6 end-to-end tests for the pin flow against a real Postgres (auto-resolve, explicit pin, no-installed-package, update-only-when-supplied, release snapshot copy, release re-resolve when live pin was null) (`StepPackagePinTests`). 516 total tests pass.

- [x] **D-7: Editor version dropdown + update notifications** —
  - **D-7.1 — Version dropdown in StepFormDialog** (done):
    - Header card shows the step's package name (read-only) + a
      `RadzenDropDown` populated from `StepPackageService.GetVersionsAsync(name)`,
      ordered highest-semver first via new public
      `StepPackageResolver.OrderByHighestSemver(...)` helper.
    - On Edit: initialises from `DeploymentStep.StepPackageName/Version`.
      On Add: auto-resolves the latest installed pin via
      `StepPackageResolver.ResolveLatestForStepTypeAsync(stepType)` and
      surfaces it in the dropdown for user override before save.
    - Picking a different version is captured on Save: the dialog now
      passes the explicit `(stepPackageName, stepPackageVersion)` pair to
      `ProcessService.AddStepAsync` / `UpdateStepAsync` (the optional
      D-6 parameters that previously only the auto-resolver populated).
    - **Update-available chip inside the dialog**: if the dropdown's
      pinned version isn't the first row (which is "latest installed"),
      a yellow "Update: {version}" badge sits next to it.
    - Process page (`Process.razor`) now renders an "Update available"
      badge next to each step card whose pinned version is lower than
      the catalog's highest semver. Backed by a per-page cache
      (`_latestInstalledByPackage`) reloaded after every step mutation.
  - **D-7.2 — Schema reload on version change + field carry-over** (done):
    - The dropdown's `Change` handler `OnPickVersionAsync` looks up the
      picked row in a cached `_versionRows` dictionary and deserialises
      its `StepPackage.UiSchemaJson` via `StepUiSchemaJson.Deserialize` —
      this is the canonical per-version schema. Falls back to
      `BuiltInStepSchemas.GetForStepType(stepType)` only when the row
      has no UI schema (legacy / unsigned dev installs).
    - Field carry-over: keeps shared field values, applies the new
      schema's declared `Default` to added fields, drops removed fields
      from the in-memory bag, and fires a single warning toast naming
      each dropped field so the user is not surprised on next Save.
      `_extraConfigKeys` (legacy hand-edited keys outside any schema)
      stay untouched — they still travel into the final Config.
    - Schema deserialization errors stop the version switch and toast
      the parse error; the dropdown stays on the previous version.
  - **Per-step "Update available" confirm dialog with changelog + schema
    diff modal**: deferred. The existing flow (yellow badge on the
    process page → opens StepFormDialog → user picks the latest version
    in the dropdown → carry-over + diff toast → Save) already covers
    the user journey end-to-end. A separate confirm-with-changelog
    modal would polish the UX but doesn't unblock any functionality.
    Pending: a `StepPackage.ChangelogMarkdown` column (CHANGELOG.md
    extracted from the archive at upload time) + a dedicated update
    dialog component. Tracked as a follow-up under D-10's umbrella.
  - **No bulk auto-upgrade.** No floating pins (no "latest 2.x" mode in v1) — exact pin only.

- [x] **D-8: Refactor existing built-ins into step packages** — every built-in extracted into its own step-package and the agent's in-DI handler path retired. Sliced D-8.1 → D-8.9.

  **D-8.1 — Infrastructure + first port (Manual)** (done):
  - Moved `IStepHandler` + `StepHandlerContext` to `KrakenDeploy.Contracts.Steps` so step packages can compile against the SDK alone (no agent dep). The old agent-namespace types collapse to global-using aliases.
  - New `steps/KrakenStepPackage.targets` — shared MSBuild target that AfterTargets="Build" lays out `manifest.json` + `executor/` + `ui/` from .csproj metadata properties (`KrakenStepPackageId`, `Version`, `DisplayName`, `StepTypes`, `ExecutorTypeName`) and zips to `bin/.../{id}-{version}.kdeploy-step`.
  - First port: `steps/KrakenDeploy.Steps.Manual` produces `octopus.manual-1.0.0.kdeploy-step`. Handler class identical behaviour to the legacy in-DI one (clean-room from Octopus docs); the in-DI handler stays in place until D-8.9 retires it.
  - Server-side `BuiltInStepPackageSeeder` scans `{contentRoot}/seed/step-packages/` on startup, installs anything new via the existing `StepPackageService.UploadAsync` with `Source = Preinstalled`. Idempotent: re-runs are cheap (name, version) lookups. Configurable via `StepPackages:SeedDirectory`.
  - `Server.csproj` takes a `<ReferenceOutputAssembly>false</ReferenceOutputAssembly>` ProjectReference to each Steps.* project so they build first, plus an `AfterTargets="Build"` copy target that gathers `steps/*/bin/.../*.kdeploy-step` into the server output's `seed/step-packages/`.
  - Tests: 4 integration tests for the seeder (fresh install, idempotency, bad-filename tolerance, missing-dir tolerance — `BuiltInStepPackageSeederTests`). 10 unit tests on the ported handler + the built archive (`ManualStepPackageTests`). 530 total tests pass.

  **D-8.2 — Octopus.SubstituteVariables port** (done): same pattern as Manual.
  Self-contained (just Octostache). Produces `octopus.substitutevariables-1.0.0.kdeploy-step`.
  9 tests cover `CanHandle`, package-required flag, single-file substitution,
  empty-target-pattern warning, dir-relative glob (`config/*.txt`), and the
  built archive's manifest shape.

  **D-8.3 — Octopus.JsonConfigurationVariables port + rename** (done): JSON
  Configuration Variables feature. **Breaking rename**: the step type was
  historically called `Octopus.FileTransform` in Kraken's schema, which
  collided with Octopus's "configuration transforms" (XDT for XML) vocabulary.
  Renamed to `Octopus.JsonConfigurationVariables` — matches what Octopus's
  own docs call this feature. XDT for XML stays where Octopus puts it: as a
  feature on `Octopus.TentaclePackage`.
  - Package: `steps/KrakenDeploy.Steps.JsonConfigurationVariables` produces
    `octopus.jsonconfigurationvariables-1.0.0.kdeploy-step`. Handler class
    `JsonConfigurationVariablesStepHandler`. No external runtime deps beyond
    `System.Text.Json`.
  - **Retired the legacy in-DI `FileTransformStepHandler`** (along with its
    DI registration and Agent.Tests block) — the package is now the only
    home for this step type. First D-8 handler to fully complete its
    in-DI → package migration.
  - Updated schemas + Razor step-type lists + docs to the new name. Solution
    and Server.csproj project paths updated to match the new project name.
  - Tests: 7 unit tests on the renamed handler + the built archive
    (`KrakenDeploy.Steps.JsonConfigurationVariables.Tests`). Old `Octopus.FileTransform`
    is now explicitly rejected by `CanHandle`. 546 total tests pass.

  **D-8.4 — Kraken.Script / Octopus.Script port** (done): biggest port so far.
  `steps/KrakenDeploy.Steps.Script` produces `kraken.script-1.0.0.kdeploy-step`
  with **two** step types in the manifest (`Kraken.Script,Octopus.Script` — first
  multi-type package; verified by the new test asserting both names appear).
  Inlined a private copy of `ScriptRunner` so the package builds against the
  SDK alone — the legacy agent-side `ScriptRunner` survives until D-8.6 because
  `KrakenIis` + `OctopusWindowsService` still depend on it. Also added
  `Microsoft.Extensions.Logging.Abstractions` to `Directory.Packages.props` for
  central package management. **Retired** the legacy in-DI `ScriptStepHandler`
  + its DI registration + the 3 Script tests in `Agent.Tests/StepHandlerTests.cs`.
  14 unit tests cover the handler's `CanHandle` matrix, the static preamble
  builders for all 5 supported languages (PowerShell / Bash / Python / C# / F#),
  PowerShell single-quote escape parity with the legacy handler, and the
  manifest's two-element `stepTypes` array.

  **D-8.5 — Octopus.TentaclePackage port** (done):
  `steps/KrakenDeploy.Steps.OctopusTentaclePackage` produces
  `octopus.tentaclepackage-1.0.0.kdeploy-step`. Brings the heaviest feature
  surface so far — `CustomDirectory` copy + purge (with exclusions),
  `ConfigurationVariables` (appSettings / connectionStrings XML rewrite),
  `ConfigurationTransforms` (XDT via `Microsoft.Web.XmlTransform`). Carried
  the existing 19-test `OctopusTentaclePackageStepHandlerTests` file over from
  `KrakenDeploy.Agent.Tests` into the package's own test project via `git mv`
  so history follows. **Retired** the legacy in-DI handler + DI registration
  + the now-stale `using KrakenDeploy.Agent.Deployment.Package;` in
  `Agent/Program.cs`. 552 total tests pass across the solution.

  **D-8.6 — Kraken.IIS + Octopus.IIS + Octopus.WindowsService + Steps.Common**
  (done): the heaviest slice yet.
  - New shared library `steps/KrakenDeploy.Steps.Common/` carries `ScriptRunner`
    — the canonical home converged here once the last in-DI consumer migrated.
    Script / KrakenIis / OctopusWindowsService all ProjectReference it.
  - Extended `KrakenStepPackage.targets` to gather every non-`KrakenDeploy.Contracts`
    DLL from `$(OutputPath)` into `executor/`. Previously the target copied only
    `$(TargetPath)`; now shared deps (`KrakenDeploy.Steps.Common.dll`,
    `Microsoft.Extensions.Logging.Abstractions.dll`, etc.) flow through.
    The agent loader's `AssemblyDependencyResolver` then finds them at runtime
    via the package's `.deps.json`.
  - `steps/KrakenDeploy.Steps.KrakenIis/` produces `kraken.iis-2.0.0.kdeploy-step`
    — second multi-step-type package (`Kraken.IIS,Octopus.IIS`). Handler
    dispatches internally via `OctopusIisConfig.IsOctopusShape`. Includes
    `KrakenIisStepHandler` + `IisScriptGenerator` (553 LOC) + `OctopusIisConfig`
    (474 LOC). Migrated 43 tests (`KrakenIisConfigTests` + `OctopusIisConfigTests`)
    out of Agent.Tests into the package's own test project.
  - `steps/KrakenDeploy.Steps.OctopusWindowsService/` produces
    `octopus.windowsservice-1.0.0.kdeploy-step`. Includes
    `OctopusWindowsServiceStepHandler` + `WindowsServiceConfig` + `WindowsServiceScriptGenerator`.
    Migrated 33 tests (`WindowsServiceConfigTests`) over from Agent.Tests.
  - **Retired the agent-side `ScriptRunner`** entirely — the canonical home is
    now `KrakenDeploy.Steps.Common`. The handlers that used it (`KrakenIis`,
    `OctopusWindowsService`) became step packages in the same slice, so the
    agent-side type has no remaining consumers. Removed its DI registration
    + the `KrakenDeploy.Agent.Deployment.Iis` + `KrakenDeploy.Agent.Deployment.Service`
    using imports from `Program.cs`.
  - Agent in-DI handler list now: `SubstituteVariablesStepHandler` +
    `ManualInterventionStepHandler` only. Both are already shipped as step
    packages (D-8.1, D-8.2); D-8.9 retires the last two in-DI registrations
    along with the fallback branch in `DeploymentExecutor.ResolveHandlerAsync`.
  - 552 total tests still pass — handler instance counts move between
    assemblies as tests follow the source.

  **D-8.9 — Retire in-DI handler path entirely** (done):
  - Removed the last two in-DI handler files (`ManualInterventionStepHandler`,
    `SubstituteVariablesStepHandler`) plus their `AddTransient<IStepHandler, ...>`
    registrations in `Agent/Program.cs`.
  - Removed the `IEnumerable<IStepHandler> stepHandlers` constructor parameter
    and the `_handlers` field from `DeploymentExecutor`. The `ResolveHandlerAsync`
    fallback branch (`_handlers.FirstOrDefault(h => h.CanHandle(...))`) is gone;
    `DeploymentExecutor` now refuses to dispatch any step that doesn't carry a
    valid `(StepPackageName, StepPackageVersion)` pin and logs an actionable
    error pointing at the missing package install.
  - Deleted the two type-alias shim files in
    `KrakenDeploy.Agent.Deployment.StepHandlers/` (`IStepHandler.cs`,
    `StepHandlerContext.cs`) that existed solely to keep the old namespace
    importable; nothing references it any more. Cleaned the leftover
    `using KrakenDeploy.Agent.Deployment.StepHandlers;` imports across
    `Program.cs`, `DeploymentExecutor.cs`, `StepPackageLoader.cs`, and the
    Agent.Tests test files — they now import `KrakenDeploy.Contracts.Steps`
    directly.
  - Deleted `tests/KrakenDeploy.Agent.Tests/StepHandlerTests.cs` whose 11
    tests duplicated the package-test-project coverage in
    `Steps.Manual.Tests` + `Steps.SubstituteVariables.Tests`.
  - 547 total tests pass — D-8 ends with the agent shipping zero
    hardcoded step handlers; every step type is package-backed.
  - `KrakenDeploy.Steps.KrakenIis` → `kraken.iis-2.0.0.kdeploy-step` (the existing `KrakenIisStepHandler` + `IisScriptGenerator` + `KrakenIisConfig` + the C-5 schema).
  - `KrakenDeploy.Steps.OctopusIis` → `octopus.iis-1.0.0.kdeploy-step` (the B-3 `OctopusIisConfig` mapper; package is separate because step type is distinct, even though the script-emit reuses the Kraken.IIS generator via an inter-package reference — handled cleanly by ALC sharing).
  - `KrakenDeploy.Steps.OctopusTentaclePackage` → `octopus.tentaclepackage-1.0.0.kdeploy-step` (B-1).
  - `KrakenDeploy.Steps.Script` → `kraken.script-1.0.0.kdeploy-step` (handles both `Kraken.Script` and `Octopus.Script`).
  - `KrakenDeploy.Steps.SubstituteVariables` → `octopus.substitutevariables-1.0.0.kdeploy-step`.
  - `KrakenDeploy.Steps.JsonConfigurationVariables` → `octopus.jsonconfigurationvariables-1.0.0.kdeploy-step` (renamed from `Octopus.FileTransform` in D-8.3 to match Octopus's vocabulary).
  - `KrakenDeploy.Steps.Manual` → `octopus.manual-1.0.0.kdeploy-step`.
  - Agent's `Program.cs` stops registering these handlers in DI. Server fresh-install seed (new `seed/step-packages/` directory) contains the built-in zips; first-run server-startup code copies them into the data dir (so out-of-the-box every install has the same preinstalled set). Each package gets its own test project asserting `manifest.json` parses, signature verifies, and `IStepHandler.CanHandle` returns expected types.

- [x] **D-9: GitHub-feed sync** —
  - `StepPackageCatalogEntry` aggregate (Name, Version, DownloadUrl, Sha256, ManifestJson, Changelog, PublishedUtc, ReleaseHtmlUrl, LastSyncedUtc) + EF migration `AddStepPackageCatalog` (unique `(name, version)` index, secondary index on `name`). Platform-level (added to `SpacesTests` excluded list).
  - `StepPackageCatalogService.RefreshAsync` calls `GET /repos/{owner}/{repo}/releases?per_page=100` via the shared `kraken.github` named `HttpClient` (lifts the 60 req/hr limit when `GitHub:Token` is set). Per release: skips drafts + prereleases, finds the `.kdeploy-step` asset, extracts the manifest from the release notes' fenced ```json``` block, extracts the SHA-256 from a ```sha256``` block OR an inline `SHA-256: <hex>` line, upserts by `(name, version)`, removes orphans. Configurable via `StepPackages:Catalog:Owner` (default `KrakenDeploy`), `:Repo` (default `StepPackages`), `:Enabled` (default true).
  - `InstallAsync(name, version)` downloads the asset with `Accept: application/octet-stream`, hashes to a temp file, verifies SHA against the catalog row, then re-streams through `StepPackageService.UploadAsync` with `StepPackageSource.CatalogPull`. SHA mismatch refuses install loudly. Emits `AuditEventType.StepPackageInstalled`.
  - `StepPackageCatalogPollJob` Hangfire recurring (`kraken.step-package-catalog-poll`, hourly); swallows network errors at Warning so a flaky GitHub doesn't trigger retries.
  - REST: `GET /api/step-package-catalog`, `POST /refresh`, `POST /{name}/{version}/install`. View gated by `StepPackageView`; install/refresh by `StepPackageManage`.
  - UI: `/step-packages` Razor page wraps content in two `RadzenTabsItem`s — **Installed** (existing grid with D-11 uninstall) and **Catalog**. Catalog rows carry a status badge ("Installed" green / "Available" blue), a "View release" link, an Install button (permission-gated), plus a "Refresh now" toolbar alongside the last-sync timestamp.
  - 6 integration tests in `StepPackageCatalogServiceTests` drive a stubbed `HttpMessageHandler` against fake `/releases` responses — covers manifest extraction, draft/prerelease filtering, asset-missing skip, parse-failure counting, orphan cleanup, and the disabled-by-config no-op. 559 total tests pass.

  **Required convention for the public `KrakenDeploy/StepPackages` repo's GitHub Releases:**
  - One Release per `(package id, version)` pair.
  - Release notes embed the manifest as a fenced `\`\`\`json ... \`\`\`` block.
  - Plus either a fenced `\`\`\`sha256 <hex> \`\`\`` block OR a single-line `SHA-256: <hex>` directive.
  - Release tag / asset filename otherwise free-form; the service picks the `.kdeploy-step` asset by extension.

- [x] **D-10: Bulk-upgrade admin tool** —
  - `StepPackageUsage` aggregate (PackageName + VersionGroup[] + UsageRow{StepId, project, step name, step type, IsRunbook}) and `BulkUpgradeResult` (touched count + skipped rows with reason) in `KrakenDeploy.Server.Core.Domain.StepPackages`.
  - `StepPackageService.GetUsageAsync(name)` runs two indexed EF predicates (on `deployment_steps.step_package_name` and `runbook_steps.step_package_name`), joins each to project metadata, groups by pinned version, sorts highest-first. Released snapshots are deliberately excluded — they're immutable.
  - `StepPackageService.BulkUpgradeAsync(name, targetVersion, deploymentStepIds, runbookStepIds)` validates the target version exists in the catalog, then loops the supplied IDs flipping `StepPackageVersion`. Per-row outcomes: `Touched` count vs `Skipped` list with reason (`"not-found"` for race-with-delete, `"already-target"` for no-op). Single `SaveChangesAsync` so the whole batch lands atomically. Released snapshots untouched.
  - REST: `GET /api/step-packages/{name}/usage` (view-gated), `POST /api/step-packages/{name}/bulk-upgrade` (manage-gated, takes `BulkUpgradeRequest { TargetVersion, DeploymentStepIds, RunbookStepIds }`). On success emits `AuditEventType.StepPackageBulkUpgraded` carrying touched + skipped counts; the per-step row mutations are also picked up by the existing `AuditableEntityInterceptor` so individual change history survives.
  - UI: new `/step-packages/{name}/usage` Razor page. Click the package name in the installed grid → page lists every live step pinned to any version of that package, grouped into `RadzenCard`s by pinned version with per-group "Select group" / "Clear group" buttons. Per-row checkboxes drive a `_selected` HashSet. Toolbar carries a `RadzenDropDown` of all installed versions of the package (highest semver first) + a "Bump selected" button that POSTs to the bulk-upgrade endpoint, toasts the result, reloads. Project name in each row links back to the project's process or runbook page.
  - Razor file is named `StepPackageUsagePage.razor` (not `StepPackageUsage.razor`) to avoid the C# symbol collision with the domain record of the same name.
  - 5 integration tests in `StepPackageBulkUpgradeTests` cover the usage query (mixed deployment + runbook rows across two versions), the actual bump for deployment vs runbook steps, the skipped-row reasons (`already-target` + `not-found`), and the target-must-exist precondition. 564 total tests pass.
  - **Deferred**: pre-apply schema-delta preview (added/removed/changed-widget fields) called out in the original spec. The D-7.2 carry-over toast already covers the single-step flow at edit time — a bulk-flow preview would be a nice-to-have polish but no functionality is blocked.

- [x] **D-11: Uninstall + conflict detection** (done) —
  - `StepPackageService.UninstallAsync(name, version)` returns a tristate `UninstallResult { Uninstalled | Blocked | NotFound }`. On `Blocked`, carries a `StepPackageUsageReport` listing every live `DeploymentStep` + `RunbookStep` pinned to the version (with project name/slug + step name + isRunbook flag) plus every release whose `ProcessSnapshot` contains a matching pin. Live-step queries are indexed EF predicates; release scan pulls + filters in C# (no JSONB containment in EF translation; release counts are bounded).
  - `StepPackageUsageReport` lives in `KrakenDeploy.Server.Core.Domain.StepPackages`.
  - `DELETE /api/step-packages/{name}/{version}` returns 204 on clean uninstall, 409 + the report body on blocked, 404 when no such row. On success it emits `AuditEventType.StepPackageUninstalled` via `IAuditLog`. Gated by `Permission.StepPackageManage`.
  - `/step-packages` Razor page gets a per-row delete button. Two-stage confirm: a simple "Uninstall?" prompt → if the service returns conflicts, the dialog opens a Radzen Alert with a grouped human-readable report ("Project X (slug): step1, step2 (runbook); releases: 1.0.0, 1.1.0"). No bulk-upgrade affordances yet — that's D-10. Successful uninstall toasts + reloads the grid.
  - On clean uninstall the row is deleted from the DB and `{dataPath}/step-packages/{name}/{version}/` removed from disk (best-effort — disk failure logs a warning but doesn't roll back the DB change).
  - Agent-side cached copies are NOT actively purged — they sit until the cache TTL or a manual sweep (deferred to a future `kraken cache prune` CLI verb).
  - 4 new integration tests in `StepPackageUninstallTests` cover NotFound, clean uninstall (DB row + disk dir removed), blocked by live step, blocked by release snapshot. 553 total tests pass.

- [x] **D-12: Authoring guide + dev experience** (done) —
  - **Signature recipe fix**: `StepPackageManifestJson.CanonicalSignatureInput` now requires the executor DLL's SHA-256 (`ReadOnlySpan<byte>`, 32-byte length-checked) — previously the recipe was documented to include it but didn't, which would have allowed a swapped-DLL attack against an otherwise valid manifest signature. Canonical input is now exactly `UTF-8(JSON(manifest.WithoutSignature())) ‖ SHA-256(executor.dll)`.
  - **`StepPackageSigner` SDK helper** (`src/KrakenDeploy.Contracts/StepPackages/StepPackageSigner.cs`): `Sign(manifest, executorDllPath, RSA privateKey)` → returns a signed manifest carrying base64 signature; `Verify(manifest, executorDllPath, RSA publicKey)` → returns `VerifyResult(IsValid, Reason)`; PEM import helpers `ImportPublicKeyFromPem` / `ImportPrivateKeyFromPem`. RSA-SHA256 PKCS#1 v1.5. Public-only key signing throws `CryptographicException` (the failure mode a tampered server should hit).
  - **Real verification on server**: `StepPackageService.VerifySignatureAsync` extracts the executor DLL from the uploaded zip into a temp path, loads `StepPackages:TrustedPublicKey` from configuration as PEM, calls `StepPackageSigner.Verify`. Dev-mode allowlist (`StepPackages:AllowUnsignedUploads = true` AND `signedBy == "kraken-project"` AND `signature == "unsigned-dev-build"`) bypasses verification with a Warning log — that's the seed-zip path. Production refuses unsigned uploads loudly.
  - **Real verification on agent**: `StepPackageLoader.VerifySignature` mirrors the server-side path against `StepPackages:TrustedPublicKey` from the agent's config. Same dev-mode sentinel respected.
  - **Authoring guide** (`docs/step-packages.md`): scaffolding (`dotnet new krakenstep`), manifest field reference table, `IStepHandler` contract + lifecycle, UI schema vocabulary, signing setup (`openssl genrsa -out kraken-signing.key 4096` + `openssl rsa -pubout` + signing recipe diagram + `StepPackageSigner.Sign` C# snippet + server `StepPackages:TrustedPublicKey` PEM config + dev-mode allowlist), local testing (`curl -F file=@... /api/step-packages`), GitHub catalog publishing convention (fenced manifest JSON + fenced sha256 OR `SHA-256:` directive + `.kdeploy-step` asset), stable SDK surface reference table.
  - 9 new round-trip tests in `StepPackageSignerTests`: sign+verify with matching key pair, wrong key rejected, tampered executor DLL rejected, tampered manifest rejected, unsigned manifest rejected, malformed base64 rejected, missing executor file rejected, public-only key fails to sign, PEM public-key round-trip. Loader-test fixtures switched from the dummy `"fake-base64-sig"` to the real `"unsigned-dev-build"` sentinel so they exercise the actual dev-mode path. New `CanonicalSignatureInput_rejects_executor_sha_of_wrong_length` covers the length precondition. 574 total tests pass.
  - **Deferred** (documented as follow-ups, none blocking D-track completion):
    - ~~`kraken pack` CLI verb in `KrakenDeploy.Cli`~~ — **done in D-12.1** (see below).
    - ~~Sample step-package with a non-trivial "AWS S3 upload" example~~ — **done in D-12.2** (see below).
    - ~~`StepPackage.ChangelogMarkdown` column extracted from `CHANGELOG.md` at upload~~ — **done in D-12.4** (see below).
    - ~~MSBuild signing integration~~ — **done in D-12.5** (see below).
    - **MUST: smoke-test the AWS S3 sample with real AWS credentials against a real S3 bucket.** All current tests use the `FakeS3Uploader` — they prove the handler logic, the credential-resolution rules, and the disposal contract, but they DO NOT exercise `Amazon.S3.AmazonS3Client.PutObjectAsync` against real S3. Required before recommending the sample as production-ready:
        1. Provision a test bucket (e.g. `kraken-deploy-step-test`) in a region close to the test host — `eu-central-1` recommended for LAUS.
        2. Create an IAM user with `s3:PutObject` + `s3:PutObjectAcl` scoped to that bucket only (no `s3:ListBucket`, no wildcards). Issue an access key pair.
        3. Stand up a Kraken server + agent locally. Upload `kraken.steps.aws-s3-upload-1.0.0.kdeploy-step` via the UI or `curl`.
        4. Author a project / process with the step, bind `Kraken.AwsS3.AccessKeyId` + `Kraken.AwsS3.SecretAccessKey` to sensitive variables, set BucketName + Region + ObjectKeyPrefix. Stage a small package payload (a few KB of text files).
        5. Run a deployment. Verify: (a) files appear in S3 under the expected key prefix, (b) deployment log streams "Uploaded <file> → s3://… (<bytes> bytes)" lines as each upload completes, (c) `uploaded.json` lands in the deployment's artifacts with the correct shape, (d) cancelling mid-batch aborts cleanly without partial-bucket corruption.
        6. Repeat with `ContinueOnError = True` + a deliberately invalid object key to confirm per-file failure tolerance.
        7. Repeat the happy-path run on an EC2 instance with both credential keys BLANK to confirm the default-credential-chain path picks up the instance IAM role.
        8. Rotate / revoke the test IAM user's keys after the smoke test. **Do NOT commit the keys or attach them to the test issue.** Sanitize logs before sharing externally — they contain bucket names and (if anything leaks) credential prefixes.
    - GitHub Actions workflow YAML (lives in the separate `KrakenDeploy/StepPackages` repo, not this one).

- [x] **D-12.1: `kraken pack` CLI verb** (done) —
  - New `PackCommands` in `src/KrakenDeploy.Cli/Commands/PackCommands.cs`. Two argument shapes dispatched by extension: `.csproj` → runs `dotnet build -c $(configuration)`, locates the produced `.kdeploy-step` under `bin/$configuration/*/`, then optionally signs in place; `.kdeploy-step` → skips build and just re-signs. Without `--key` it's a no-op build (the dev sentinel signature emitted by `KrakenStepPackage.targets` stays in place — fine for local iteration with `AllowUnsignedUploads`, never for production).
  - Signing path: opens the zip, reads `manifest.json`, extracts the executor DLL to a temp path (the `StepPackageSigner.Sign` API is file-path-based), computes the canonical signature input + RSA-SHA256, rewrites `manifest.json` inside the zip via `ZipArchiveMode.Update`. Public `SignArchive(source, dest, pemPath)` so authoring tools can call the same code path without spawning the CLI.
  - CLI csproj now references `KrakenDeploy.Contracts` so the verb can call `StepPackageSigner` + `StepPackageManifestJson` directly — same recipe the server + agent verify against, no second implementation to drift.
  - 8 new tests in `tests/KrakenDeploy.Cli.Tests/PackCommandsTests.cs`: sign-then-verify round-trip against a fresh RSA key, explicit `--output` leaves source archive intact, FileNotFound for missing inputs / missing PEM, InvalidData for archives missing `manifest.json`, RunAsync error paths for missing input and unknown extension, RunAsync no-op happy path on an existing archive. 590 total tests pass (was 574 after D-12 + the 8 new pack tests + 8 from D-12.2).

- [x] **D-12.2: AWS S3 upload sample step package** (done) —
  - `examples/AwsS3UploadStepPackage/` — a non-trivial reference package demonstrating the patterns this guide teaches. Package id `kraken.steps.aws-s3-upload`, step type `Kraken.Steps.AwsS3Upload`. **Works against real S3 out of the box** — the sample bundles `AWSSDK.S3` v4 and `dotnet build` produces a working `.kdeploy-step`.
  - Handler `AwsS3UploadStepHandler` walks `context.ExtractDir`, enumerates files matching `Kraken.AwsS3.FileGlob`, uploads each to S3 with `Kraken.AwsS3.ObjectKeyPrefix`, streams a log line per completed file via `context.LogAsync`, writes `uploaded.json` to `context.ArtifactsDir`, honors `ContinueOnError` semantics, rethrows `OperationCanceledException` so an aborted deployment marks correctly.
  - Production uploader `AwsSdkS3Uploader` wraps `Amazon.S3.AmazonS3Client` (v4). `IS3Uploader : IAsyncDisposable` + `await using var uploader = …` in the handler so the S3 client gets disposed at end-of-step. `ResolveCredentials(accessKeyId, secretAccessKey)` is a public static so tests can pin the contract: both populated → `BasicAWSCredentials`; both blank → `null` (SDK default credential chain — env vars / shared file / EC2 instance role); only one populated → `ArgumentException`. Asymmetric-credentials configuration is caught by `TryParseConfig` and surfaced as a config-error log line; the handler never reaches the uploader's throwing path.
  - Test seam: `internal` ctor taking `Func<S3UploadConfig, IS3Uploader>` + `[InternalsVisibleTo]` lets the test project drive the handler with a fake uploader.
  - `AWSSDK.S3` v4.0.16 added to `Directory.Packages.props` under the "Sample dependencies" group (main KrakenDeploy binaries don't depend on it). The `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` knob was first set on this csproj, then promoted into `steps/KrakenStepPackage.targets` in D-12.3 so every step package gets reliable runtime-DLL bundling.
  - UI schema (`ui-schema.json`) covers all step config fields with realistic widgets — `text`, `select` for canned ACL, `checkbox` for ContinueOnError, `variable` for the access keys (marked `sensitive: true`).
  - `examples/AwsS3UploadStepPackage/README.md` walks readers through: what the sample demonstrates (mapped to authoring-guide concepts), credential handling, build + sign + upload commands, the produced archive layout, copy-this-for-other-AWS-services pointer.
  - 16 tests in `tests/AwsS3UploadStepPackage.Tests/` cover: CanHandle case-insensitivity, missing-config failure, no-match-glob warning (success), happy-path upload + artifact manifest, hard failure aborts batch (default), ContinueOnError tolerates per-file failures, cancellation propagation, missing ExtractDir, asymmetric credentials rejected, both-blank announces default chain, both-populated announces explicit credentials, uploader disposed after batch, and 4 `AwsSdkS3Uploader.ResolveCredentials` cases (both-keys → BasicAWSCredentials, both-blank-variants → null, both asymmetric cases throw).
  - `docs/step-packages.md` gets a new "Reference example" section linking to the sample with a mapping table (pattern → location in the handler) and a "Bundling third-party DLLs" callout. Its "Sign your package" section leads with `kraken pack --key signing.key` instead of the manual 30-line snippet (the snippet is kept further down as a fallback for constrained CI). Local-testing section now mentions `kraken pack` as the production path. 598 total tests pass (was 590 after the original D-12.2; +8 for the AWS SDK + credential-resolution paths).

- [x] **D-12.3: Reliable runtime-DLL bundling for every step package** (done) —
  - Pre-existing leak the AWS S3 sample exposed: every step package's `.kdeploy-step` archive shipped with ONLY the project's own DLL — NuGet runtime deps weren't being copied. The early Octostache-using packages (Manual, SubstituteVariables, OctopusTentaclePackage) worked incidentally because the agent host also references `Octostache` and the D-4 ALC delegation fell back to the default ALC. AWSSDK.S3 isn't in the agent host, which is how the AWS sample surfaced the gap. Future packages depending on any non-agent-hosted NuGet would have silently broken the same way.
  - Fix: `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` promoted from the AWS sample's csproj into `steps/KrakenStepPackage.targets`'s top-level `<PropertyGroup>`. Every step package importing this targets file now correctly copies NuGet runtime DLLs into `bin/$(Configuration)/$(TargetFramework)/`, where the pack target globs them into `executor/`.
  - The pack target's `Exclude` was expanded from `KrakenDeploy.Contracts.dll` only to also skip `Google.Protobuf.dll`, `Grpc.*.dll`, and `Microsoft.Extensions.Logging.Abstractions.dll`. Those are deterministic Contracts-transitive (or generic-host) deps the agent host process always has, so bundling them would just add ~1 MB of dead weight per package. The D-4 ALC delegation resolves them from the default ALC anyway, with type identity preserved.
  - Verified each existing step package now bundles its expected runtime deps: Manual / SubstituteVariables / OctopusTentaclePackage / KrakenIis / OctopusWindowsService ship `Octostache.dll` + its transitives (Markdig, Newtonsoft.Json, Sprache, Octopus.Versioning, MEL.Caching/DI/Options/Primitives); OctopusTentaclePackage additionally ships `Microsoft.Web.XmlTransform.dll`; AwsS3UploadStepPackage ships `AWSSDK.S3.dll` + `AWSSDK.Core.dll`. JsonConfigurationVariables (no NuGet runtime dep beyond Contracts) correctly carries only its own DLL.
  - Redundant `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` removed from `examples/AwsS3UploadStepPackage/AwsS3UploadStepPackage.csproj` since the imported targets file now handles it.
  - Pinning tests added: `Built_archive_bundles_Octostache_runtime_DLL` + `Built_archive_excludes_agent_hosted_runtime_DLLs` in `ManualStepPackageTests`; `AwsS3UploadArchiveTests.Built_archive_bundles_AWSSDK_S3_and_Core_runtime_DLLs` in the AWS sample's test project. These fail loudly if anyone ever turns the flag off or adds an agent-hosted dep to the bundle.
  - `docs/step-packages.md` "Bundling third-party DLLs" callout rewritten to reflect that the targets file handles this centrally; authors don't need any csproj-level configuration for their NuGet runtime deps to land in the archive. 601 total tests pass (was 598 after D-12.2; +3 archive-content pinning assertions).

- [x] **D-12.4: `StepPackage.ChangelogMarkdown` column + UI** (done) —
  - New nullable `ChangelogMarkdown` column on the `StepPackage` aggregate (`text` in Postgres, no length cap at the DB level since the upload path bounds bytes — see below). EF migration `AddStepPackageChangelogMarkdown` lands the column.
  - `StepPackageService.UploadAsync` now reads `CHANGELOG.md` from the zip root via `StepPackageFiles.ChangelogFileName` (constant introduced earlier in D-1) and persists the content. Uses a new `ReadTextWithCapAsync(maxBytes: 256 * 1024)` helper so a malicious or accidentally-huge changelog can't bloat the DB row; oversized files are truncated with a trailing "`…truncated at 262 144 bytes.`" marker so operators see why the text ends abruptly.
  - REST passthrough: the existing `GET /api/step-packages` endpoint returns the entity directly, so adding the column auto-exposes it — no DTO churn.
  - UI surfaces:
    - **`StepFormDialog.razor`**: the existing "Update available" badge gets a sibling icon button (`description` Material icon) that toggles a compact `RadzenCard` showing the latest installed version's changelog as pre-formatted text. Off by default — keeps the dialog tight for users who don't care about release notes; one click reveals up to 240 px of scrollable changelog when investigating an upgrade.
    - **`StepPackages.razor`**: new "Changelog" column on the installed grid with a description-icon button per row. Clicking pops the changelog into a `RadzenDialog.Alert`. Rows whose package shipped no `CHANGELOG.md` show a grey em-dash with a tooltip explaining why.
  - Rendering decision: plain pre-formatted text inside a `<pre style="white-space:pre-wrap">` rather than full Markdown-to-HTML. Avoided pulling Markdig into the server's NuGet surface for a feature this small; typical changelogs (`## 1.2.0`, bullet lists) are scan-readable as raw text. Documented as a future polish target.
  - 3 new integration tests in `StepPackageChangelogTests` (Server.Data.Tests) drive the new path through Postgres: archive ships a `CHANGELOG.md` → contents persist verbatim; archive omits the file → column stays `null`; archive ships a 300 KB changelog → contents are truncated with the marker, total length stays just past the 256 KB cap. 604 total tests pass (was 601 after D-12.3).

- [x] **D-12.5: MSBuild signing integration** (done) —
  - New `SignKrakenStepPackage` target in `steps/KrakenStepPackage.targets` runs `AfterTargets="PackKrakenStepPackage"` and signs the just-built archive in place when `$(KrakenSigningKey)` is set. Authors get one-step `dotnet build -p:KrakenSigningKey=./signing.key` ergonomics — no separate `kraken pack` invocation needed in CI.
  - CLI resolution strategy: the target prefers the in-repo built CLI (`src/KrakenDeploy.Cli/bin/{Debug,Release}/net10.0/kraken.dll`) so the in-tree `steps/*` projects sign without needing the CLI installed globally; falls back to `dotnet kraken` when no in-repo build exists (the external Kraken.SDK consumer path — documented as `dotnet tool install -g KrakenDeploy.Cli`).
  - Hard-errors with a clear MSBuild error message when `KrakenSigningKey` is set but the path doesn't exist, so CI configuration typos fail loudly at build time instead of silently shipping unsigned archives.
  - The target's `Condition="'$(KrakenSigningKey)' != ''"` means it's a true no-op when the property is empty — local iteration with the dev sentinel signature stays free.
  - 2 new end-to-end tests in `MsBuildIntegrationTests` (Cli.Tests) spawn a real `dotnet build` of the Manual step package with and without `KrakenSigningKey` set. With key: the produced archive's signature is NOT the dev sentinel and verifies cleanly against the matching public key via `StepPackageSigner.Verify`. Without key: the dev sentinel stays in place. Together they pin both the happy-path and the no-op contract; an XML typo in the targets file caused a "An XML comment cannot contain '--'" parse failure during development that this test caught immediately.
  - `docs/step-packages.md` "Sign on every build via MSBuild" section added between the `kraken pack` CLI section and the manual `StepPackageSigner` fallback. Documents the CLI resolution chain + the `dotnet tool install -g KrakenDeploy.Cli` prerequisite for external authors. 606 total tests pass (was 604 after D-12.4; +2 MSBuild integration assertions).

**Phase D — closed.** D-1 through D-12 done in 4 main commits, plus D-12.1 (kraken pack CLI), D-12.2 (AWS S3 sample), D-12.3 (runtime-DLL bundling fix), D-12.4 (changelog), D-12.5 (MSBuild signing). Open follow-ups carried as separate tasks (none blocking): live S3 smoke-test, GitHub Actions workflow YAML in the public KrakenDeploy/StepPackages repo.

### M11 — AI integration (MCP, autonomous diagnosis, ad-hoc agent actions, process assistant)

Five top-level sub-features sharing one `IKrakenAi` abstraction over `Microsoft.Extensions.AI.IChatClient`. Pluggable providers via `IChatClient` adapters: Anthropic (official `Anthropic` NuGet), OpenAI / Azure OpenAI (`Microsoft.Extensions.AI.OpenAI`), DeepSeek (OpenAI-compatible endpoint), local Ollama / LM Studio. Default provider: **Anthropic Claude** (cleanest tool-use + structured-output story); admin picks per-Space at runtime.

**Architecture decisions (locked):**
- **Per-Space API keys.** Each Space stores its own `Ai:Provider` + `Ai:ApiKey` + `Ai:Model` + monthly budget cap. Costs attribute to the Space; one Space's exhausted budget doesn't block another's deployments.
- **Token-count audit only by default.** Every AI call writes `AiCallLog { provider, model, promptTokens, completionTokens, latencyMs, featureTag, spaceId, userId? }`. Full prompt + response bodies are NOT stored unless an admin flips `Ai:LogPromptBodies = true` per Space — the audit table is a juicy GDPR target with bodies on.
- **Sanitisation at the `IKrakenAi` wrapper.** Variable values marked `Sensitive` are stripped before any prompt leaves the process. Variable NAMES + non-sensitive values stay so the LLM has enough context to be useful. Sanitisation events get an audit row (the metadata of what was scrubbed, never the value).
- **Default provider = `Disabled`.** AI features are off until an admin sets a provider + key. No global LAUS key shipped in source; every installation supplies its own.
- **DeepSeek data-residency warning surfaced in the settings UI** (the provider hosts in China). Same UI mechanism as the `AllowUnsignedUploads` warning.
- **Agents NEVER call LLMs directly.** All AI calls route through the server. Reasoning: production-target nodes in segmented AD networks don't have egress to api.anthropic.com / api.openai.com; punching N firewall rules per agent is a security non-starter. Server already has egress for catalog polling; one extra outbound destination. Centralised audit + budget + key management + sanitisation as a side benefit.
- **Skipped: AgentBlazor.** Single-maintainer beta, MudBlazor coupling (we use Radzen), wrong shape for our actual UX (schema-aware sidebar, not floating chat). All assistant UI is bespoke Radzen.

**Sequencing:** M11.A → M11.B → M11.C → M11.E → M11.D. Each ships as a separate phase.

- [ ] **M11.A: Shared AI infrastructure** —
  - **M11.A.1** New `src/KrakenDeploy.Ai/` library. `IKrakenAi` interface wrapping `Microsoft.Extensions.AI.IChatClient`. DI registration per provider via `KrakenAiProvider` enum: `Anthropic`, `OpenAI`, `AzureOpenAI`, `DeepSeek`, `LocalOpenAiCompatible` (Ollama / LM Studio), `Disabled`.
  - **M11.A.2** Provider adapters: Anthropic uses `Anthropic 12.22+`'s `.AsIChatClient("claude-…")`; OpenAI / Azure OpenAI / DeepSeek all use `Microsoft.Extensions.AI.OpenAI`'s client pointed at the provider's base URL. Single code path for OpenAI-compatible providers.
  - **M11.A.3** `AiCallLog` aggregate (Space-scoped) + EF migration. Columns: `Provider`, `Model`, `FeatureTag` (`Diagnosis`, `Mcp`, `Adhoc`, `Assistant`), `PromptTokens`, `CompletionTokens`, `LatencyMs`, `Success`, `ErrorMessage?`, `UserId?`. Plus opt-in `PromptBody?` + `ResponseBody?` columns gated by `Ai:LogPromptBodies`.
  - **M11.A.4** Sanitisation layer: `IPromptSanitizer` strips `Sensitive`-flagged variable values from any string passed into `IKrakenAi`. Tested against the Variables table + the Octostache substitution surface. Audit row records what was scrubbed (variable name + the deployment / project / Space it came from), never the value.
  - **M11.A.5** Per-Space monthly budget cap (`Ai:BudgetUsdPerMonth`). `IKrakenAi` checks current Space's month-to-date cost against the cap before every call; exceeded → clear `BudgetExceededException` that callers handle without crashing the deployment. Cost = tokens × per-1k rate (rate table embedded in source, updated as providers change pricing).
  - **M11.A.5.2** **Per-Space rate overrides** (extends M11.A.5). New `AiCostOverride` aggregate (Space-scoped, unique on `(SpaceId, Provider, Model)`) lets operators override the embedded rate table for billing-visibility accuracy (custom EA pricing, etc.). New `DbBackedAiCostCatalog` wraps the embedded `AiCostCatalog` and checks per-Space overrides first. UI surface lives inside the M11.A.6 settings page as a "Cost overrides" section with an editable grid. CRUD via `GET/POST /api/spaces/{id}/ai-settings/cost-overrides` + `DELETE` per `(provider, model)`.
  - **M11.A.6: Settings page** (Space-scoped) — `/s/{spaceSlug}/settings/ai`, new `Pages/SpaceSettings/AiSettings.razor`. Section layout:
    - **Provider:** dropdown (Disabled / Anthropic / OpenAI / AzureOpenAI / DeepSeek / LocalOpenAiCompatible), model picker, API key field (masked + `[Show]` button), BaseUrl (shown for Azure / Local).
    - **Budget:** monthly cap input + live MTD spend readout (read-only, refreshed on page focus).
    - **Features:** four checkboxes — Diagnosis (M11.C), MCP (M11.B), Adhoc (M11.E), Assistant (M11.D). All default off.
    - **Audit:** `LogPromptBodies` toggle with GDPR footer.
    - **Cost overrides:** grid editor wiring to M11.A.5.2.
  - **Storage**: new `SpaceAiSettings` aggregate (one row per Space, PK is `SpaceId`). API key stored encrypted via the existing `IEncryptionService` (AES-256-GCM, same primitive that protects `Sensitive` variable values).
  - **Save semantics**: empty `ApiKey` field on PUT preserves the existing value (operator editing only the model shouldn't accidentally clear the key); explicit `[Clear]` button sets to null. Provider change without key re-entry stays valid.
  - **REST endpoints**: `GET /api/spaces/{id}/ai-settings` (api key masked), `PUT /api/spaces/{id}/ai-settings` (preserves existing key on empty field), `GET /api/spaces/{id}/ai-settings/api-key` (returns decrypted — audit-logged per call), `GET /api/spaces/{id}/ai-settings/usage` (MTD breakdown by feature).
  - **Permissions**: split `SpaceAiSettingsView` (see provider + budget + MTD readout, masked key) and `SpaceAiSettingsManage` (edit, reveal key, manage cost overrides). Matches the existing `StepPackageView/Manage` precedent.
  - **Audit events**: `SpaceAi.SettingsUpdated` (before/after redacted), `SpaceAi.ApiKeyRevealed` (every key reveal — operators reading the key IS a sensitive operation), `SpaceAi.CostOverrideAdded/Removed`.
  - `DbKrakenAiSettingsProvider` (`KrakenDeploy.Server.Data`) reads the row + decrypts on the request path. Replaces the current no-op registration so M11.C/D/E callers transparently get real settings.
  - **M11.A.7** DeepSeek + (any OpenAI-compatible non-EU endpoint) surfaces a "**This provider routes prompts to <region>. Not recommended for state-institution data**" warning banner in the settings page. Folded into the M11.A.6 Razor page; no separate sub-task.
  - **M11.A.8** `docs/ai-integration.md`: the data-flow diagram, sanitisation rules, GDPR posture, budget mechanics, key-rotation procedure, troubleshooting. Plus a "which provider for LAUS" decision table.

- [ ] **M11.B: MCP server** (closes the "external AI tools can talk to Kraken" story) —
  - **M11.B.1** New `src/KrakenDeploy.Mcp/` library using the official `ModelContextProtocol` 1.3.0 + `ModelContextProtocol.AspNetCore` packages (co-maintained Microsoft + Anthropic). Hosted in-process inside the server on a new HTTP+SSE endpoint.
  - **M11.B.2** Resources (read-only): `kraken://deployments/{id}/log` (full log), `kraken://deployments/{id}/artifacts/{name}`, `kraken://targets/{name}/health` (heartbeat + last failure reason), `kraken://releases/{projectSlug}/{version}` (release manifest), `kraken://step-packages/{name}/{version}/manifest`. All gated by existing `Permission.*View` checks via the existing API-key auth.
  - **M11.B.3** Tools: `list_failed_deployments(envName?, projectSlug?, sinceHours?)`, `get_deployment_log(deploymentId)`, `get_deployment_diff(deploymentId)` (structured delta vs last green run — variables changed, package version bumped, target patched, etc.), `get_target_health(targetName)`, `retry_deployment(deploymentId)` [requires `DeploymentExecute`], `get_release_history(projectSlug, count?)`, `query_targets(role?, environment?)`, `get_step_config(deploymentId, stepIndex)`. Permission gates same as REST.
  - **M11.B.4** Standalone `kraken-mcp` exe (new `src/KrakenDeploy.Mcp.Cli/` project) that proxies stdio↔HTTP. Lets Claude Desktop / Cursor / Copilot Chat connect to a remote Kraken server via the local stdio MCP protocol. Auth via existing API key.
  - **M11.B.5** Tests against the [MCP inspector](https://github.com/modelcontextprotocol/inspector) for protocol compliance + integration tests that spin the server + a stdio client and round-trip each tool. Audit log entry per tool call.
  - **M11.B.6** `docs/mcp.md`: quick-start (one-liner config for Claude Desktop / Cursor / Copilot Chat), tool reference, permission matrix, troubleshooting.

- [ ] **M11.C: Autonomous failure diagnosis** —
  - **M11.C.1** `DeploymentDiagnosis` aggregate (Space-scoped) + EF migration. Fields: `DeploymentId`, `ProbableCause` (text), `Confidence ∈ {Low, Medium, High}`, `SuggestedFix` (text), `RelevantLogLinesJson` (`[{line: 42, text: "…"}]`), `ModelUsed`, token counts. One row per failed deployment.
  - **M11.C.2** Context assembler: full log (tail-of-failure focus — last 200 lines + step boundaries), failed step config (post-sanitisation), target info (OS, last successful deploy timestamp), output of `get_deployment_diff` vs last green run. Reuses the same diff logic as M11.B.3's tool.
  - **M11.C.3** Hangfire job `DeploymentDiagnosisJob`. Triggered from `DeploymentWorker` after `Status → Failed`. Calls `IKrakenAi.CompleteAsync<DeploymentDiagnosis>` with structured-output schema, persists. Async, doesn't block deployment finalisation. Exponential backoff on transient LLM errors.
  - **M11.C.4** "AI Analysis" card on the deployment-failure detail page. Renders above the log: probable cause, confidence badge, suggested fix. Confidence-Low cases show a "AI guess — verify yourself" footer. "Show in log" links highlight the relevant log lines.
  - **M11.C.5** Optional webhook push (Slack / Teams) when `Notifications:Failure:WebhookUrl` is set in the Space. Body = diagnosis summary + link to the failed deployment.

- [ ] **M11.E: Ad-hoc agent actions** (natural-language → server-generated PowerShell → operator-approved → agent runs) —
  - **Architecture: B2 (script-handoff).** Server takes prompt + target set, LLM generates a PowerShell script, operator approves, server signs the approved script, agents verify the signature and execute via the existing script-execution machinery. Agents have ZERO AI awareness — they see a signed script command, same shape as a deployment step. B1 (LLM-driven tool calls back into agents per turn) is intentionally deferred to a later release; B2 is auditable, the script is human-readable, and approval breaks the prompt-injection loop.
  - **M11.E.1** `POST /api/adhoc-actions` endpoint. Body: `{ prompt, targetSelector, mode }`. `targetSelector` resolves once to a frozen set of target ids — same selector shapes as deployments: by role(s), by tag(s), or by explicit id list. **The frozen set is the session's target scope for its entire lifetime — every iteration runs on the same set; the LLM cannot change it.** If the operator wants to act on a different set (single machine, smaller subset), they pick that set upfront before starting the session. `mode ∈ {readonly, mutating}`; default `readonly`. RBAC: new `Permission.AdhocActionsExecute`. Per-Space rate limits.
  - **M11.E.2** Generation pipeline: server LLM call with target context in the system prompt (target OS list, available roles, target health snapshots, the relevant package layout). Output is JSON-shaped via structured-output: `{ description, generatedScript, expectedOutputShape, riskAssessment, requiresMutation }`.
  - **M11.E.3** Static analysis gate. AST-level PowerShell parser (reusing `KrakenDeploy.Steps.Common`'s ScriptRunner machinery) rejects the generated script if it contains: `Invoke-Expression`, `Invoke-Command -ComputerName` (the agent runs ON the target — no remoting needed), `Remove-Item -Recurse -Force`, `Stop-Service` / `Remove-Service` (without explicit mutation flag), service install/uninstall, registry writes, file I/O outside designated paths. For `mode=readonly`: only `Get-*` / `Test-*` / `Measure-*` cmdlets allowed.
  - **M11.E.4** Operator approval dialog: Radzen syntax-highlighted PowerShell block, the frozen session target list (display-only — already locked in M11.E.1), risk assessment from the LLM, estimated duration. Three buttons: "Approve" / "Edit and approve" (textbox lets operators tweak before approving) / "Reject". One approval covers the script's run on the entire session target set; no per-target subset toggles.
  - **M11.E.5** **Single-approver rule (locked).** Same model as deployments today: `Permission.AdhocActionsExecute` + Space membership is enough. Two-person rule deferred — revisit if production incidents motivate it.
  - **M11.E.6** Script signing on approval. Server signs the approved script using the existing step-package signing-key infrastructure (D-12 `StepPackageSigner`-style recipe with a separate `Adhoc:SigningKey` config slot). Agent verifies before execution; mismatched signatures rejected loudly.
  - **M11.E.7** Dispatch via existing SignalR agent transport. Each target runs the script; results stream back. Server collates per target.
  - **M11.E.8** Optional narrative summary: a SECOND LLM call with the results as input → human-readable summary ("3/5 nodes healthy; node-04 has 4 GB free on C:; node-05 timed out"). This is summarisation only, NOT an execution loop — no LLM-driven follow-up actions.
  - **M11.E.9** UI page `/adhoc`: prompt input, target selector, approval dialog, live result rendering, audit trail. Radzen.
  - **M11.E.10** Expose adhoc-action as an MCP tool (`run_adhoc_action`) so external AI clients can drive the same flow. Approval gate still enforced server-side regardless of the source.
  - **M11.E.11** Per-target `RiskLevel ∈ {Dev, Staging, Production}` column on the target entity. Production targets in `mode=mutating` get a clearer warning banner in the approval dialog. Two-person rule lives behind a feature flag for later activation.
  - **Iteration loop — operator-approved per-turn (M11.E.12–M11.E.17):**
    The single-shot path above only handles the happy case. When a script errors out on some targets, operators want the LLM to look at the failure output and propose a fix. This is the "B1" pattern from the original threat model — but with **operator approval at every iteration over a frozen target set chosen upfront**, it's iterated-B2: each turn is a fresh approval gate, same safety contract as the first turn, and the LLM never expands the blast radius beyond the targets the operator originally picked. No autonomous escalation possible.
  - **M11.E.12** `AdhocSession` + `AdhocIteration` aggregates (Space-scoped) + EF migration. `AdhocSession { Id, Prompt, Mode, FrozenTargetSetJson, Status ∈ {Active, Closed, CapReached, BudgetExceeded, OperatorStopped}, CreatedBy, SpaceId, MaxIterations }`. `AdhocIteration { SessionId, IterNumber, GeneratedScript, ScriptSignature, LlmModel, LlmTokens, ResultsJson, OperatorWhoApproved, ApprovedAtUtc }`. One row per turn. The session's `FrozenTargetSetJson` is set ONCE on creation (resolved from the operator's roles/tags/explicit-ids selector) and used by every iteration unchanged — that's why iterations don't carry a per-row target list. Audit trail is the table itself.
  - **M11.E.13** Iteration LLM call. After each iteration's agent results stream back, server makes a SECOND LLM call with `{ originalPrompt, mode, priorIteration.script, perTargetResults: [{ target, exitCode, stdout, stderr }] }`. Structured output via `IChatClient.CompleteAsync<IterationVerdict>` where `IterationVerdict { Verdict ∈ {AllSucceeded, NoFixAvailable, ProposeFix}, Narrative, ProposedScript?, ProposedScriptDescription?, RiskAssessment? }`. The LLM gets per-target results so it can REASON about which targets failed and write a script that's defensive (e.g. only acts when a condition is true), but it CANNOT propose target-set changes — the session's target set is frozen and the LLM has no field to influence it. `AllSucceeded` + `NoFixAvailable` close the session; `ProposeFix` opens the next iteration's approval dialog.
  - **M11.E.14** Iteration cap enforcement. Default `5` per session, override via per-Space `Ai:Adhoc:MaxIterationsPerSession`. On cap, session auto-closes with `Status = CapReached` and a clear "manual intervention required — N iterations did not resolve" log entry. Prevents runaway loops if the LLM keeps proposing broken scripts.
  - **M11.E.15** Invariants enforced across all iterations: (a) **target-set immutability** — session's `FrozenTargetSetJson` is set ONCE on creation; no iteration can change it, the LLM has no surface to propose a change, and every iteration's script is dispatched to exactly that set; (b) **mode immutability** — a session started `readonly` cannot have any iteration propose a `mutating` script; the static-analysis gate rejects mode escalation; (c) same allowlist + AST checks on every iteration's script (M11.E.3 applies to v2, v3, … same as v1); (d) cumulative budget enforcement — each iteration's LLM call counts against the Space's monthly cap; (e) signing on every iteration (M11.E.6 applies to v2, v3, … same as v1). Operators who want a smaller scope start a fresh session with a narrower target set.
  - **M11.E.16** UI: `/adhoc` page is session-based. Each session renders as a card tree: prompt + target set at the root, then one expandable card per iteration. Each iteration card shows the generated script (syntax-highlighted), per-target results stream, LLM's narrative for that iteration. Three buttons per iteration: `Approve next iteration` (if LLM proposed a fix) → applies to the session's frozen target set / `Stop session` (close with `OperatorStopped`) / `Mark resolved` (close with `Closed`). No per-target subset selector — target set was chosen upfront and is read-only for the session's lifetime. Audit-trail view per session.
  - **M11.E.17** Tests: round-trip a session that succeeds on iteration 1 (`AllSucceeded`); a session where iteration 1 partly fails, iteration 2's script is idempotent and re-runs on the full set, all targets reach the desired state (`ProposeFix` → operator approves → `AllSucceeded`); a session that hits the iteration cap (`CapReached`); a session where the LLM tries to escalate from `readonly` to `mutating` (static-analysis gate must reject the iteration's script); a session where the LLM is given results from a frozen set of 5 targets and tries to reference a target outside that set in its script — server's dispatcher rejects targets not in `FrozenTargetSetJson`. Property-based test: across 50 randomly-generated multi-iteration sessions, the static-analysis gate trips on every script that contains a forbidden cmdlet, regardless of which iteration produced it.
  - **Out of v1 scope** (documented): autonomous remediation (LLM acting without operator approval at any turn); persistent cross-session agent memory (each session is independent); per-target divergent scripts within a single iteration (one script per iteration runs on the full frozen set; if the operator needs different actions on different subsets, they start separate sessions with narrower target selectors). These are the remaining high-risk patterns; their absence is intentional.

- [ ] **M11.D: Process builder assistant (UI)** —
  - **M11.D.1** Step suggester (one-shot). New-project / empty-process screens get a "Suggest process from package" button. AI inspects the selected package's payload manifest (top-level dirs, .csproj names, web.config / .service / static asset presence) → proposes a starter step list with rationale. Structured output. User confirms or edits before save.
  - **M11.D.2** Script editor sidebar. Right-rail of `Process` step edit when step type is `Octopus.Script` / `Kraken.Script`. AI suggestions as user types (debounced 800 ms). Context fed in: available variables (sanitised), target type, package layout, current script body. Streaming responses via `IKrakenAi.StreamChatAsync`. Multi-turn within the edit session; state component-local, not persisted.
  - **M11.D.3** Field-level explanations. AI icon next to each step-config field. Click → contextual explanation (one-shot, cached per `(stepType, fieldKey, projectId)` for the page session). Uses the schema field's `Help` / `Description` plus project + target context.

**M11 dependencies + risks:**
- M11.A blocks everything (the wrapper, the audit, the sanitisation).
- M11.B depends only on M11.A; ships fast, high external leverage.
- M11.C depends on M11.A + M11.B (uses `get_deployment_diff`).
- M11.E depends on M11.A + M11.B (the static-analysis + signing + dispatch matures during MCP work; expose adhoc as an MCP tool in M11.E.10).
- M11.D depends on M11.A only but is the heaviest UI work; do last.

**Open follow-ups carried across all of M11** (none blocking):
- Live LLM-provider smoke-test against real API keys (each provider needs at least one end-to-end "send prompt, get response, verify token-count audit row" run before declaring production-ready).
- Cost-per-1k-token rate table maintenance — providers update pricing periodically; the embedded table needs a refresh procedure.
- Prompt-template registry — initially prompts live in source per-feature; if they grow numerous, extract to a database-backed registry with version pinning per Space.

### M12 additinal polish 
OpenTelemetry export to Grafana stack or Seq.

### M13 — Configuration & Admin UX

Planning pass triggered by a walk-through of Octopus Deploy's `/configuration/*` surface (25 sub-sections). M13 brings KrakenDeploy's operator-facing admin surface up to parity with the parts of Octopus's catalog we actually need. Sequenced after M11; we'll start M13 once M11 closes.

**Scope-shaping decisions (locked):**
- **Six top-level groups instead of Octopus's flat 25-item sidebar.** Octopus's flat list works for them but bloats the nav. We group by what the sub-page actually IS (status vs config vs identity vs crypto vs notifications vs behaviour) — easier to find things, easier to gate-by-permission cleanly.
- **Per-instance vs per-Space split surfaced in the nav.** Some things are inherently per-Space (AI settings, webhooks). Others are inherently instance-wide (license, server-cert thumbprint). Octopus puts everything under one "Configuration" — for our multi-Space model we make the scope visible.
- **Out of scope** (documented explicitly so we don't drift): **Nodes** (KrakenDeploy is single-node; multi-node is M-Scale if ever), **Thumbprint** (we use SignalR + gRPC, not Tentacle's cert-handshake), **Git as config source** (separate config-as-code milestone), **Let's Encrypt ACME wizard** (operators handle TLS via their reverse proxy; we don't ship the wizard).
- **Reuse before rebuild.** Several sub-pages are thin glue over services we already have. Test Permissions is the clearest example — `IPermissionEvaluator` is built (Server.Data/Services/PermissionEvaluator.cs); the UI is just user-picker + scope-picker + permission grid + a single `HasPermissionAsync` call per cell.

**Group structure:**

- [ ] **M13.A: Audit & Diagnostics** (highest LAUS value; quickest path to operator self-service) —
  - **M13.A.1** `Audit.razor` polish — date-range picker (Last 7d / 30d / 90d / custom), advanced filters (user, event type, subject type, IP), export to CSV/JSON. The data model + permission gate (`EventView`) already exist; this is UI work on top of the existing query path.
  - **M13.A.2** `/configuration/diagnostics` page — server info card (OS, .NET runtime, working set, thread count, uptime, build commit hash), "Run integrity check" button (validates EF schema + key invariants like Space-scoped row counts, FK consistency), "Download System Diagnostics Report" button that bundles config + recent log tail + DB row counts into a zip (sanitised — no API keys, no Sensitive variable values).
  - **M13.A.3** `/configuration/maintenance` — single `MaintenanceMode` flag (instance-wide). When on, write endpoints reject with 503 + a clear "instance under maintenance" message unless the caller holds a new `BypassMaintenance` permission. Existing background jobs (Hangfire) pause on the flag. New audit events `Maintenance.Enabled` / `Maintenance.Disabled`.

- [ ] **M13.B: Notifications & event routing** —
  - **M13.B.1** `/configuration/smtp` — Host / Port / Timeout / TLS-mode / From / Auth fields, **"Save and test" button** that sends a probe email and surfaces the result inline (failure mode is "operator pastes wrong port and never gets notifications" — a test button kills that class of bug).
  - **M13.B.2** Generalised event subscription system. `EventSubscription` aggregate (Space-scoped): name, event-type filter list (`Deployment.Failed`, `Deployment.Succeeded`, `Release.Created`, etc.), team/user recipients, transport (email via M13.B.1, webhook URL), schedule (immediate vs digest every N hours). The M11.C diagnosis webhook becomes one specific subscription, not its own bespoke wiring.
  - **M13.B.3** `/configuration/subscriptions` page — list / filter / add / edit / pause. Per-subscription "Trigger test event" button for the same reason as M13.B.1's test button.

- [ ] **M13.C: Identity & Access polish** (lots of small wins, low risk) —
  - **M13.C.1** **Test Permissions page** — `/configuration/test-permissions`. User picker → scope picker (Space dropdown + optional Project/Environment/Tenant) → grid of every Permission with green-tick / red-cross + the "why" (which Role × Team Membership granted or denied it). Thin wrapper on existing `IPermissionEvaluator.HasPermissionAsync` (Server.Data/Services/PermissionEvaluator.cs:42). High admin-quality-of-life, low implementation cost.
  - **M13.C.2** User Invites — code-based invitation flow. Operator generates N single-use codes scoped to specific teams (multi-select), codes expire in 48h. Invitee enters code at `/register/{code}` → creates account + auto-joined to the teams. Right shape for on-prem networks where email delivery isn't reliable (state-institution context). New `UserInvite` aggregate + `Permission.UserInviteManage`.
  - **M13.C.3** Service-account distinction on `Users.razor`. Add `User.Kind ∈ {Human, ServiceAccount}` discriminator + "View service accounts only" filter toggle. Service accounts are API-key-only (no password, no SSO claim mapping). Pin the contract that Hangfire jobs + MCP tool calls attribute to service accounts, not phantom humans.
  - **M13.C.4** API Keys cross-user admin view. We have `ApiKeyView` / `ApiKeyViewAll` permissions already (Permission.cs:1300-1311). New "My API Keys" ↔ "All API Keys" toggle on the page header, with the cross-user list showing User / Purpose / Hint-prefix-only / Created / Expires badge. Hint format `API-XXXX•••••••` (never the full key).
  - **M13.C.5** Three-tier admin role split. Today we have one "admin" notion (`ConfigureServer` permission). Split into:
    - **System administrator** — everything, including license + signing key rotation.
    - **System manager** — instance-wide read + most writes, but NOT license / signing keys / encryption master key.
    - **Space manager** — full power within one Space, no instance-wide visibility.
    Codify by adding new permission groupings; existing single-admin role gets migrated to System administrator. Operators who want delegated admin (the common LAUS case) can grant System manager to junior admins without exposing crypto rotation.
  - **M13.C.6** Filter-state callout on list pages — `Filtering by: <space> includes <foo>` strip above the result grid. Apply to Users / Teams / API Keys / Audit. Already done on Step Packages; spread the pattern.

- [ ] **M13.D: Code Signing Keys + Encryption Master Key** —
  - **Terminology note (critical, easy to miss):** Octopus's "Signing Keys" sidebar entry is for **OIDC token signing keys** — RSA keys that Octopus uses when it acts as an OIDC *issuer* for its own resource APIs (auto-rotate every 90 days; no UI to manage because OIDC issuer is EAP). **That does NOT apply to KrakenDeploy** because KrakenDeploy is an OIDC *client* (we consume external IdPs via `IdentityProvider`), not an issuer. The keys we DO manage are code-artifact signing keys, an entirely different domain. We name our page "Code Signing Keys" to avoid the conceptual conflict; "Signing Keys" matching Octopus's vocabulary would mislead.
  - **M13.D.1** `/configuration/code-signing-keys` — unified UI for the keys M11.A / D-12 introduced: **step-package signing key** (D-12, `StepPackageSigner` + `StepPackages:TrustedPublicKey`), **adhoc-script signing key** (M11.E.6). Lists Active / Expired rows with id, created, expiry. **"Rotate"** button generates a new key, marks the old one Expired (still validates incoming requests for a configurable grace window — default 90 days). **"Revoke"** dropdown on expired rows immediately invalidates. Rotation writes a new audit event type per key kind.
  - **M13.D.2** Encryption-master-key rotation runbook + UI. Today the AES-256-GCM master key in `appsettings.json` is fixed; rotating it breaks all encrypted columns (sensitive variables, AI API keys). The UI gates a re-encryption job: new key supplied, background job iterates `Variable` + `SpaceAiSettings` + (future) `AiCostOverride` rows decrypting with old key + re-encrypting with new key. **High-risk operation** — requires double-confirmation + maintenance mode (M13.A.3).

- [ ] **M13.E: System & License** —
  - **M13.E.1** `/settings/license` polish — paste-license-blob field exists as a stub. Add: parsed-license display (limits + expiry + features), validation (signature check), audit on save. License model TBD — XML-signed-blob like Octopus, or JWT, or simpler? Decide when we ship licensing.
  - **M13.E.2** `/configuration/license-usage` — quota dashboard with per-quantity gauges (Projects, Tenants, Targets, Users, Task Cap) + over-limit banner + per-Space rollup table. Numbers come from existing queries; the UI is the deliverable.
  - **M13.E.3** Quota enforcement at write boundaries. Project create / Tenant create / Target register endpoints check the resolved license + current usage and refuse with a clear "license limit reached — see License Usage page" message when over.

- [ ] **M13.F: Features & Behaviour** (lower priority; mostly nice-to-haves) —
  - **M13.F.1** Per-instance feature toggle panel (`/configuration/features`). Bool toggles grouped by topic: Feeds, Steps, Onboarding, Help. **Not the same as the per-Space AI feature flags** — those stay per-Space. This is for things like "Community Step Templates enabled", "Onboarding wizard shown to new users", etc.
  - **M13.F.2** **Global Deployment Freezes** — `/configuration/freezes`. Define windows where deployments are blocked across **multiple projects** simultaneously (release weeks, holidays, maintenance lockdowns). Octopus added this specifically because per-project freezes led to inconsistent enforcement + manual overhead; KrakenDeploy mirrors that shape. Each freeze: name, start/end window, **scope selector** (all projects in Space / specific projects / specific environments / tag selector for tenanted deploys) + optional `OverrideFreeze` permission for emergency-bypass roles. `DeploymentWorker` consults the freeze table before starting; failed start gets a clear "blocked by freeze 'X' until Y" message in the deployment log. **Delete the existing `Pages/ProjectPages/Freezes.razor` placeholder** (11-line "pending" stub at `/projects/{slug}/freezes`) — Octopus's lesson is freezes should be global, not per-project.
  - **M13.F.3** Performance knobs — worker concurrency, queue depth, slow-step threshold for warnings. Today these are `appsettings.json`-only.
  - **M13.F.4** Audit-log retention policy. Today `ai_call_logs` + `audit_entries` grow unbounded. Add retention setting (per category) + a background sweep job that deletes rows older than the retention window. Critical for LAUS GDPR posture — "show me everything you have about user X" is currently unbounded.

- [ ] **M13.G: Backup & Restore** —
  - **Massive correction after audit**: backup + restore are **substantially built already** as **CLI commands**, not a fresh greenfield as the original M13 plan assumed. `src/KrakenDeploy.Server/Commands/BackupCommands.cs` (205 lines) + `RestoreCommands.cs` (178 lines) wire `dotnet KrakenDeploy.Server.dll backup --to <dir>` and `… restore --from <dir>` into the Server entry-point switch (Program.cs:57-60). Documented in `docs/on-prem-guide.md` with cron + Task Scheduler examples.
  - **What `backup` already does**: spawns `pg_dump --clean --if-exists` (auto-detects PostgreSQL 15/16 on Windows + `/usr/bin/pg_dump` + `which pg_dump` on Linux), writes `database.sql`; copies `Server:DataPath` (packages, artifacts, agent binaries) into a sibling `data/`; emits `manifest.json` with timestamp + server version + connection-info; bundles all three into `kraken-backup-{yyyyMMdd-HHmmss}/`.
  - **What `restore` already does**: reads + validates `manifest.json`; **rejects on server-version mismatch** with a clear "downgrade first" message; runs `psql -v ON_ERROR_STOP=1 -f database.sql`; prompts on data-dir overwrite (`y/N`); copies data dir back.
  - **Real M13.G gap (much smaller than originally scoped):**
    - **M13.G.1** UI page `/configuration/backup` that triggers the existing backup logic on-demand from the running server (without requiring shell access). Two modes: "Backup now" (writes to a configurable on-server target dir; shows a progress + final-path notification) and "Schedule" (Hangfire recurring job that calls the same backup code path — slots into `HangfireJobRegistrar` next to the existing 6 jobs).
    - **M13.G.2** Backup target abstraction. Today output is always a local directory (`--to <dir>`). For S3 / SMB targets we'd lift the inner "where the bundle goes" into an `IBackupTarget` interface (mirrors the established `IArtifactStore` pattern + comment about future S3/Azure swap). `AWSSDK.S3` is already in CPM from M11/D-12.2 — no new dep.
    - **M13.G.3** Backup health dashboard — last-successful timestamp + size + duration; last N runs with status. Just a query over a new `BackupRun` audit-log row (or reuse `audit_entries` with a `Backup.Completed` event type).
    - **M13.G.4** Encryption-master-key warning callout in the UI — "this backup cannot be decrypted without the same `Encryption:MasterKey`." Already mentioned in `docs/on-prem-guide.md` but never surfaced visually.
  - **Restore remains CLI-only** — restore-from-UI in a deployment orchestrator is too dangerous (server can't restore itself while it's running; needs to be stopped, the binary version aligned, then restore). The on-prem guide's CLI workflow stays the source of truth.
  - Effort: ~half day for the UI + scheduled-job path (reuses existing logic); ~half day for `IBackupTarget` + S3 impl. Much smaller than the original "3-5 days" estimate.

**M13 nav restructure (proposed)**

Today the sidebar has "Configuration" with five flat items (Spaces / Users / Teams / Roles / Identity Providers). M13 expands it to a grouped panel:

```
Configuration
├── Audit & Diagnostics
│   ├── Audit              (have, polish)
│   ├── Diagnostics        (new — M13.A.2)
│   └── Maintenance        (new — M13.A.3)
├── System
│   ├── License            (stub, polish — M13.E.1)
│   ├── License Usage      (new — M13.E.2)
│   ├── Backup             (new — M13.G)
│   └── Performance        (new — M13.F.3)
├── Identity & Access
│   ├── Users              (have, polish — M13.C.3)
│   ├── Teams              (have, polish — M13.C.6)
│   ├── User Roles         (have)
│   ├── API Keys           (partial — M13.C.4)
│   ├── User Invites       (new — M13.C.2)
│   ├── Spaces             (have)
│   ├── Identity Providers (have)
│   └── Test Permissions   (new — M13.C.1; quick win)
├── Crypto & Keys
│   ├── Signing Keys       (new — M13.D.1)
│   └── Encryption Master  (new — M13.D.2; high-risk)
├── Notifications
│   ├── SMTP               (new — M13.B.1)
│   └── Subscriptions      (new — M13.B.2/3)
└── Behaviour
    ├── Features           (new — M13.F.1)
    └── Freezes            (new — M13.F.2)
```

**Recommended ordering inside M13:**
1. **M13.C.1** Test Permissions (quick win — `IPermissionEvaluator` already built).
2. **M13.A.3** Maintenance mode (small surface, immediate operational value).
3. **M13.A.2** Diagnostics page (operator self-service for support tickets).
4. **M13.B.1+B.2+B.3** SMTP + Subscriptions (unblocks M11.C webhook delivery cleanly).
5. **M13.A.1** Audit polish.
6. **M13.C.4** API Keys admin view.
7. **M13.C.2** User Invites.
8. **M13.C.3** Service-account distinction.
9. **M13.C.6** Filter-state callouts (cosmetic; sweep).
10. **M13.D.1** Signing Keys.
11. **M13.F.4** Retention policy (GDPR-critical).
12. **M13.E.x** Licensing (depends on the licensing model decision).
13. **M13.D.2** Master-key rotation runbook (high risk; do once everything else is mature).
14. **M13.G** Backup (largest scope; do last).
15. **M13.F.1-3** Features / Freezes / Performance (polish; do as needed).

**M13 open questions** (resolve before each sub-task lands):
- License model: XML-signed-blob (Octopus pattern) vs JWT vs simpler?
- Quota enforcement: hard refuse vs grace banner (the Octopus approach lets you go over limit + show a banner; some installations prefer hard refuse).
- Subscription channels v1: email only, or email + webhook from day one?
- Encryption master rotation: do we ship the runbook + UI together, or runbook first / UI later?

### M13 — audit of what's already built vs gap (per-section)

After walking through Octopus's full /configuration/* surface (25 sub-sections) and a systematic broad-grep + CLI-command + Hangfire-job inventory on 2026-05-22, the per-feature reality vs the original M13 plan is much more favourable than the plan suggested. Two audit passes were needed — the first missed the `BackupCommands` CLI surface entirely because the initial grep was service-focused, not command-focused; the second pass also caught existing freeze-page-stub + offline-drop SMTP fields + offline-drop webhook delivery path + Hangfire dashboard route + 30 EF migrations. Captured here so the implementation order can pick the cheapest wins first.

**Inventory snapshot (2026-05-22):**
- 30 EF migrations applied.
- 5 CLI verbs wired into the Server entry-point switch (Program.cs:51): `users`, `database`, `backup`, `restore`. No others.
- 6 Hangfire recurring jobs registered (`HangfireJobRegistrar.cs`): `audit-retention` (daily 03:00 UTC, 365-day window), `agent-last-seen-offline` (every 5 min), `registration-token-expiry` (daily 02:00, FOR AGENT TARGETS not user invites), `scheduled-deployment-dispatch` (every minute), `step-template-catalog-poll` (hourly), `step-package-catalog-poll` (hourly).
- Hangfire dashboard mounted at `/hangfire` with `HangfireDashboardAuthFilter` gating access.
- `/healthz` endpoint: DB connect + target count + connected-agent count. No deeper diagnostics page.
- **SMTP**: `OfflineDropConfig` has SMTP fields (per-target offline-drop bundle delivery) but **no actual `SmtpClient` / `MailKit` / `System.Net.Mail` import anywhere** — the offline-drop email path defines the FIELDS but the send code doesn't ship.
- **Webhook**: `DropBundleService.DeliverViaWebhookAsync` does HTTP POST for offline-drop bundle delivery (reusable plumbing, but tied to offline-drop semantics — not a generic event-subscription webhook).
- **Subscription**: 3 matches, all noise (Azure subscription IDs in `OctopusSystemVariablesBuilder`, an IServerLink comment). No `EventSubscription` aggregate.
- **Freeze**: `Pages/ProjectPages/Freezes.razor` exists as an 11-line placeholder ("Deployment freezes — pending"). Other freeze references in the codebase are about release-variable-snapshot ("release freezes its variables at cut time") + step-package-pin-snapshot, NOT deployment-blocking windows.
- **Maintenance**: `AgentUpdateConfig.MaintenanceWindowStart/End` is for the AGENT's self-update window — UNRELATED to a server-wide MaintenanceMode flag.
- **Feature flags**: 7 matches are all the AI feature flags from M11.A (per-Space). No per-instance toggle system.
- **Performance knobs**: only Hangfire `options.WorkerCount = 4` literal in `Program.cs`. No UI, no DB-backed setting.
- **Integrity check**: 0 matches — genuinely greenfield.
- **ServiceAccount**: 0 matches — genuinely greenfield.

| M13 sub-task | Existing infrastructure (file references) | True effort |
|---|---|---|
| **M13.A.1** Audit polish | `Audit.razor` (285 lines) — date pickers, event/user/subject filters, server-side paging, sortable grid. Backed by `AuditEntry` + `AuditLogService`. `EventView` / `EventViewUnscoped` perms exist. | Small — add CSV/JSON export button + endpoint. |
| **M13.A.2** Diagnostics page | `/healthz` exists (`Program.cs:833`) — DB connect + target + agent counts. Hangfire dashboard mounted at `/hangfire` with `HangfireDashboardAuthFilter`. No deeper integrity check — broad grep for "integrity" returned 0 files. | Medium — page + integrity-check + diagnostic-zip builder. Embed a link to `/hangfire` for job-level diagnostics rather than rebuilding that surface. |
| **M13.A.3** Maintenance mode | Nothing for the server. (Agent has its own `MaintenanceWindowStart`/`End` for self-update windows — **unrelated**, do not conflate.) | Medium. |
| **M13.B.1** SMTP | **Partial.** `OfflineDropConfig` (Domain/Targets) has SMTP fields (Host, Port, UseSsl, Username, encrypted Password, Recipient, Sender) for offline-drop bundle delivery + the schema is editable per-target via `TargetDetail.razor`. **But no `SmtpClient` / `MailKit` import anywhere — the FIELDS exist, the SEND CODE does not.** | Medium — add the actual SMTP send (likely MailKit; pin TLS version + cert validation policy) + a server-wide SMTP-config page distinct from per-target offline-drop config. Don't conflate the two — one is "send notifications" (server-wide), the other is "deliver offline-drop bundles via email to a specific target's recipient" (per-target). |
| **M13.B.2/3** Subscriptions | Nothing meaningful — broad grep for "subscription" returned only Azure-subscription-ID noise. `DropBundleService.DeliverViaWebhookAsync` does HTTP POST for offline-drop bundles (HttpClient pattern reusable). | Large — full event-routing system. Reuse `DropBundleService`'s HttpClient pattern for webhook transport; the routing layer (event filters → recipient teams → transport selection) is the actual work. |
| **M13.C.1** **Test Permissions** | **>95% done.** `IPermissionEvaluator.HasPermissionAsync` + `GetPermissionsAsync(user, scope)` both exist (`Server.Core/Domain/Security/IPermissionEvaluator.cs`). `BuiltInRoles.cs` defines 8 named roles. `PermissionScope` + audit `PermissionDenied` event already wired. | **Trivial** — UI binds `GetPermissionsAsync` to a permission grid. ~1-2h. |
| **M13.C.2** User Invites | `UserInvite` permission reserved (Permission.cs:43). No entity / service. `RegistrationTokenExpiryJob` exists but is for DEPLOYMENT TARGETS (agent registration), not users. | Medium — `UserInvite` aggregate + service + flow page. |
| **M13.C.3** Service accounts | `ApplicationUser : IdentityUser<Guid>` has no Kind discriminator. | Small — column + filter toggle on Users.razor. |
| **M13.C.4** API Keys cross-user | Permissions reserved (`ApiKeyView` / `ApiKeyViewAll`). **No `ApiKey` aggregate exists** — only the single `appsettings.json:ApiKey:Key` consumed by `Auth/ApiKeyAuthenticationHandler.cs`. | **Large** — full build (entity + migration + service + per-user issue/revoke + admin "all keys" view). |
| **M13.C.5** Three-tier admin (System mgr middle tier) | `BuiltInRoles.cs` already has `SystemAdministratorId` + `SpaceManagerId` + 6 task roles. | **Tiny** — add 1 entry to `BuiltInRoles.All` (System Manager = everything except crypto rotation). ~30 min. |
| **M13.C.6** Filter-state callouts | Pattern exists on Step Packages. | Tiny — cosmetic sweep across Users / Teams / API Keys / Audit pages. |
| **M13.D.1** Code Signing Keys UI | `StepPackageSigner` + `StepPackages:TrustedPublicKey` config exist (D-12). M11.E.6 adhoc-script signing not yet built. **No `SigningKey` entity** for rotation history. | Medium — entity + page + rotation flow. |
| **M13.D.2** Master-key rotation | `AesEncryptionService` reads `Encryption:MasterKey` from config (hardcoded at startup, no rotation hook). | Large + high risk — re-encryption job over `Variable` + `SpaceAiSettings`. |
| **M13.E.1** License polish | **Surprise — fully built.** Full JWT-signed model: `LicenseClaims { CustomerName, MaxTargets, MaxUsers, ExpiresUtc, IssuedUtc, LicenseType ∈ {Trial,Full,Developer} }`. `LicenseService.ValidateLicense` (RSA-2048 sig, expiry, embedded public key). `LicenseValidationResult`. `Admin/License.razor` paste-and-validate page. `LicenseWarningBanner.razor` shared banner. License-model question resolved: **JWT, not XML**. | Small — parsed-license display + audit on save. ~1h. |
| **M13.E.2** License Usage dashboard | `LicenseService.GetLicenseWarning(currentTargetCount, currentUserCount)` exists with 90% / 100% thresholds. **But:** `LicenseWarningBanner.razor:30-32` passes **placeholder zeros** — never sees real counts. Quotas limited to MaxTargets + MaxUsers (no MaxProjects / MaxTenants / MaxTaskCap). | Small-Medium — wire real counts; add UI gauges + per-Space rollup. ~2-3h. |
| **M13.E.3** Quota enforcement | `LicenseService.GetLicenseWarning` exists but **NOTHING in the codebase calls it for refuse-on-create**. | Small — call from Project / Target / User create paths. ~1h. |
| **M13.F.1** Per-instance features panel | Nothing. | Medium. |
| **M13.F.2** Global Deployment Freezes | **Stub page exists.** `Pages/ProjectPages/Freezes.razor` is an 11-line placeholder ("Deployment freezes — pending"). Other "freeze" references in the codebase (DeploymentWorker, ReleaseService) are about release-variable-snapshot + step-package-pin-snapshot, unrelated to deployment-blocking windows. No `Freeze` aggregate, no DeploymentWorker freeze-check. | Medium — but **delete the per-project stub at `/projects/{slug}/freezes`** because Octopus's lesson is freezes should be GLOBAL not per-project. New page at `/configuration/freezes`. |
| **M13.F.3** Performance knobs UI | One hardcoded knob: `options.WorkerCount = 4` in `Program.cs` (Hangfire worker count). No other performance settings. Hangfire dashboard already at `/hangfire` exposes queue depth + processing throughput visually. | Small — move worker-count to a DB-backed setting + Razor page. Link to `/hangfire` from the same page rather than duplicating its queue-depth visualisation. |
| **M13.F.4** Audit-log retention | **Already done.** `AuditRetentionJob` registered (`HangfireJobRegistrar.cs:18`) — daily 03:00 UTC, deletes audit_entries older than 365 days (hardcoded). `AuditLogService.PurgeOldEntriesAsync` does the work. | **Tiny gap** — (a) make 365-day window configurable; (b) **new** `AiCallLogRetentionJob` for `ai_call_logs` (M11.A.3 added that table without an accompanying sweep — the deferred TODO from M11.A.3). ~1-2h total. |
| **M13.G** Backup & Restore | **Substantially built as CLI.** `Commands/BackupCommands.cs` (205 lines) + `RestoreCommands.cs` (178 lines): pg_dump wrapper + data-dir copy + manifest.json + version-checked restore. Wired into `Program.cs:57-60` switch. Documented in `docs/on-prem-guide.md` with cron + Task Scheduler examples. `IArtifactStore` + `AWSSDK.S3` (CPM) ready for the future `IBackupTarget` abstraction. | **Much smaller than scoped** — UI page that triggers the existing logic + Hangfire schedule + `IBackupTarget` for S3/SMB + health dashboard. ~1 day total. |

### M13 revised effort ordering (smallest → largest)

After the audit, the smallest items first (each ~½ day or less) become attractive quick wins to ship as a single "M13 quick polish" batch before tackling the bigger pieces:

1. **M13.C.5** System Manager built-in role — one entry in `BuiltInRoles.All`. 30 min.
2. **M13.C.1** Test Permissions page — bind `GetPermissionsAsync` to a Radzen grid. 1-2h.
3. **M13.C.6** Filter-state callouts — cosmetic sweep across 4 list pages. 1h.
4. **M13.F.4** Configurable retention window + `AiCallLogRetentionJob`. 1-2h.
5. **M13.E.3** Quota enforcement — call existing `LicenseService.GetLicenseWarning` from write paths. 1h.
6. **M13.E.2** License Usage dashboard — wire real counts + add per-Space rollup UI. 2-3h.
7. **M13.E.1** License page polish — parsed display + audit on save. 1h.
8. **M13.A.1** Audit export buttons. 2h.
9. **M13.C.3** Service-account discriminator on `ApplicationUser`. 2h.
10. **M13.A.3** Maintenance mode. 3h.
11. **M13.F.3** Performance knobs UI. 2h.
12. **M13.A.2** Diagnostics page. Half day.
13. **M13.B.1** SMTP config + Save-and-test. Half day.
14. **M13.D.1** Code Signing Keys UI. Half day.
15. **M13.F.1** Features panel. Half day.
16. **M13.F.2** Global Deployment Freezes. Half day.
17. **M13.C.2** User Invites. Half day.
18. **M13.B.2/3** Subscriptions (full event-routing system). 2-3 days.
19. **M13.C.4** API Keys (full per-user system). 2-3 days.
20. **M13.D.2** Encryption master-key rotation. 2-3 days, high risk.
21. **M13.G** Backup & Restore — UI page + Hangfire schedule + IBackupTarget. ~1 day (CLI core is already done — `BackupCommands.cs` + `RestoreCommands.cs` exist).

Items 1-9 collectively close half of M13 in roughly 2 working days. They're the candidates for a "M13 quick polish" batch landed before the heavier items.

### M13 — deeper-audit cross-cutting findings (2026-05-22, third pass)

User asked for a deeper audit to surface infrastructure that could be reused (or accidentally duplicated). Third-pass findings beyond the per-section table above:

- **No domain-event / event-bus infrastructure exists.** Greps for `EventBus`, `INotification` (MediatR-style), `DomainEvent`, `IPublisher` return zero hits. The only event-publishing pattern is `ITargetStatusNotifier` (`Server.Transport/`) — a narrow in-process bus scoped to target online/offline state changes, consumed by the Blazor UI for live status updates. **M13.B.2/3 event subscriptions** can either generalise this into an `IEventBus<TEvent>` or stand up a new generic bus alongside — but the pattern is established and small (one publisher impl + Action-based subscriber registration).
- **Two `BackgroundService` workers** in `Server.Transport`: `DeploymentWorker` + `RunbookRunWorker`. These are the natural points to emit `Deployment.Started` / `Deployment.Failed` / `Deployment.Succeeded` events when M13.B's subscription system lands. Don't write a new orchestrator — hook the existing workers' completion paths.
- **Already-encrypted columns count** (relevant to M13.D.2 master-key rotation risk): `Variable` values (sensitive), `IdentityProvider.ClientSecret`, `OfflineDropConfig.SmtpPassword` + `HmacKey`, `SpaceAiSettings.ApiKey`. M13.D.2's re-encryption job must walk all four — extend the originally-planned `(Variable + SpaceAiSettings)` walk to include `IdentityProvider` + `OfflineDropConfig`. Missing either would leave silent-decrypt-failure rows after rotation.
- **`ScheduledFor` on `Deployment`** (`Domain/Deployments/Deployment.cs:48`) + `ScheduledDeploymentDispatchJob` (every minute) — already a time-window gate at the dispatch level. M13.F.2 freezes can ride the same dispatcher hook rather than building a parallel gate: when the dispatch job picks up a scheduled deployment, it checks the freeze table first.
- **`OctopusDeploymentProcessImporter`** (260 lines) + **`OctopusSystemVariablesBuilder`** (305 lines) — existing Octopus parity surface that pre-dates this audit. Importer parses `GET /api/{spaceId}/deploymentprocesses/{id}` JSON into Kraken's process model; verbatim preserves `Octopus.Action.*` keys (dual-shape strategy). Endpoint wired at `POST /api/projects/{id}/process/import-octopus`. Step-template equivalent at `POST /api/step-templates/import-octopus-api`. **Useful for any future "migrate from Octopus" feature** — call out so we don't accidentally rebuild this.
- **`AgentConnectionRegistry`** is in-memory (`InMemoryAgentConnectionRegistry` singleton; `IAgentConnectionRegistry` interface). Consumed by `/healthz` endpoint (connected-agent count) and `AgentHub`. Worth knowing for M13.A.2 diagnostics: agent connection state is observable already; the page can surface it.
- **`DropBundleService` + `OfflineResultService`** — full offline-deployment bundle pipeline (sign, deliver via webhook / SMTP / file-share / manual, validate result bundle on return with HMAC). Reusable patterns: HMAC-signed manifest (similar to D-12's step-package signing), HTTP POST delivery (reusable for M13.B webhooks), file-share delivery (reusable for M13.G backup-to-SMB).
- **`Domain/Common/AuditableEntity`** + `AuditableEntityInterceptor` (`CreatedUtc` + `ModifiedUtc` auto-stamping on inserts/updates) + `AuditLogInterceptor` (writes `audit_entries` rows for tracked changes) + `SpaceScopingInterceptor` (auto-stamps `SpaceId` from ambient `ISpaceContext`) — three EF interceptors already wired into `KrakenDbContext`. New aggregates added in M13 (`Freeze`, `EventSubscription`, `SignedKey`, `ApiKey`, `BackupRun`) **must** inherit `AuditableEntity` + implement `ISpaceScoped` to plug into these for free — don't reinvent auditing.
- **`ServiceCollectionExtensions.AddKrakenDeployData`** (`Server.Data/ServiceCollectionExtensions.cs:22+`) is the single point where every domain service registers. M13 services slot in here next to the existing 30+ services — don't add `services.AddScoped<>` calls scattered across `Program.cs`.
- **30 EF migrations applied since 2026-04-27** — the project has roughly a month of accumulated schema work. The migration name trail reads as a milestone log: M2 / M3 / M5 / M5.5 / M6 / M7 / M8 / Spaces / ProjectGroups / RBAC / OIDC / Audit log / Scheduled deploys / Agent auto-update / Agent connection registry / Variable+set audit / User theme / Deployment output vars / StepTemplate extend / StepTemplate catalog / Deployment parent link / StepPackages (5 sub-migrations across Phase D) / AI call log / Space AI settings. **M13 work that needs a new table** can follow this naming convention; the latest M11.A migration is `20260522114504_AddSpaceAiSettings`.

**M13 sub-task refinements from this audit pass:**

- **M13.B.2/3** subscriptions: don't reinvent the event-bus shape. Either `ITargetStatusNotifier`-style narrow buses per-event (multiplied N times) OR one `IEventBus<TEvent>`. The DeploymentWorker / RunbookRunWorker BackgroundServices are the natural publication sites.
- **M13.D.2** master-key rotation: **expand the re-encryption walk** from `(Variable, SpaceAiSettings)` to `(Variable, SpaceAiSettings, IdentityProvider.ClientSecret, OfflineDropConfig.SmtpPassword, OfflineDropConfig.HmacKey)`. Plus `(future ApiKey from M13.C.4, future AiCostOverride from M11.A.5.2)`. Catalogue every encrypted column in a single place to keep the rotation procedure auditable.
- **M13.F.2** Global Freezes: hook the existing `ScheduledDeploymentDispatchJob` (per-minute) rather than building a parallel dispatch gate. Freeze check runs in the same dispatcher pass.
- **M13.G.2** Backup target abstraction: reuse `DropBundleService`'s **file-share delivery** code for SMB targets (already implemented for offline-drop bundles); reuse the **HTTP POST delivery** for S3 multi-part-upload composition. The patterns are there.
- **M13.A.2** Diagnostics: include `IAgentConnectionRegistry.Count` + per-agent last-heartbeat in the page; reuse the existing in-memory state.

**Out of scope for M13** (documented so we don't drift):
- **Octopus "Signing Keys" (OIDC token signing keys)** — Octopus's sidebar entry of this name is for RSA keys it uses when it acts as an OIDC *issuer* for its own resource APIs. KrakenDeploy is an OIDC *client* only (consumes external IdPs via `IdentityProvider`), not an issuer. Our code-signing keys for step-packages + adhoc scripts (M13.D.1) are a different concern and live under "Code Signing Keys" to keep the conceptual line clear.
- Nodes (single-node design; multi-node is M-Scale if ever).
- Thumbprint (no Tentacle handshake — we use SignalR + gRPC for agents).
- Git as config-as-code source (separate milestone — M-ConfigAsCode if pursued).
- Let's Encrypt ACME wizard (operators handle TLS via their reverse proxy / load balancer).
- Telemetry-to-vendor (Octopus uses this for usage analytics; KrakenDeploy doesn't need it — M12 OpenTelemetry export is for operator-controlled targets, not vendor reporting).
- Multi-node clustering UI.
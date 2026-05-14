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
- [ ] **5b: Parameter-driven step form for non-script templates** — generic form that renders one input per `StepTemplateParameter` based on its `ControlType` (`SingleLineText` / `MultiLineText` / `Sensitive` / `Checkbox` / `Select` / `Package`). Replaces the silent "edit via API" path for community / built-in non-script templates in Phase 5.

#### Phase 7 — Execution location

- [ ] **7a: `Octopus.Action.RunOn` enum** — promote the existing `Octopus.Action.RunOnServer` boolean to a tri-state: `Server`, `ServerOnBehalfOfTarget`, `Target`. UI: radio group in `StepFormDialog`.
- [ ] **7b: Server-side script runner** — new in-process `ServerScriptRunner` (mirror of agent's `ScriptRunner`). For `Server`, runs once per deployment in the server process. For `ServerOnBehalfOfTarget`, runs once per target with that target's variables in scope. Wire into `DeploymentExecutor` so the dispatch is transparent — `IStepHandler` calls go to either the in-process runner or are streamed to the agent depending on the step's RunOn setting.

#### Phase 8 — Referenced packages

- [ ] **8a: Step config schema** — `Octopus.Action.Package.PackageReferences` (JSON array of `{Name, PackageId, FeedId, Extract}`). Server resolves to specific versions at release-creation time.
- [ ] **8b: Agent extraction** — additional packages extract alongside the primary to `extract/refs/<Name>/`. Expose `Octopus.Action.Package[<Name>].ExtractedPath` (server-side system variable) and `OCTOPUS_REFERENCED_PACKAGE_<Name>_PATH` (agent env var).
- [ ] **8c: UI** — "Referenced Packages" section in the script-step form.

#### Built-in step pack (Octopus parity)

To be sourced from the user's own Octopus instance via `GET /api/actiontemplates?builtIn=true` (authenticated API key), transcribed into Kraken-native templates with PowerShell-based handlers. **Not** decompiled from Calamari — see [docs/architecture.md](docs/architecture.md#step-execution-model) on the clean-room policy.

- [ ] **`Octopus.IIS` parity** — extend the existing `Kraken.IIS` template's parameter set to match Octopus's `Octopus.Action.IISWebSite.*` keys 1:1 so an Octopus IIS step imports without renaming.
- [ ] **`Octopus.TentaclePackage`** — package-deploy with optional pre/post scripts, config transforms, structured config-variable replacement, custom install dir.
- [ ] **`Octopus.DeployRelease`** — server-side orchestrator step: "deploy release of project X to environment Y". Requires Phase 7b (server-side runner).
- [ ] **`Octopus.Manual`** — already exists in M9. Verify parameter shape matches.
- [ ] **`Octopus.FtpUpload`, `Octopus.AzureFunction`** etc. — long tail; transcribe as Argosy/WebArgosy processes need them. Azure / AWS / Kubernetes packs deferred.

### M11 — AI integration (MCP server, autonomous diagnosis, process assistant)
Three features sharing a common `IAiProvider` abstraction (pluggable: Anthropic, OpenAI, Azure OpenAI — user supplies API key) and a shared MCP tool layer.

**MCP server (`KrakenDeploy.Mcp` project):** Exposes KrakenDeploy to any MCP-compatible AI agent (Claude, Copilot, Cursor, etc.) via stdio or HTTP+SSE transport, authenticated with the same API key as the REST API. Resources: deployment logs, artifacts, target status, release history. Tools: `get_deployment_log`, `list_failed_deployments`, `get_target_health`, `retry_deployment`, `get_release_history`, `get_step_config`, `query_targets`, `get_deployment_diff` (structured delta between a failing run and the last successful one — variables changed, package version bumped, target OS patched, etc.).

**Autonomous failure diagnosis:** Hangfire job triggered on deployment failure. Assembles a context packet (full log, failed step config, target info, `get_deployment_diff` output) and calls the configured AI provider. Stores a structured `DeploymentDiagnosis` — probable cause, confidence level, suggested fix, relevant log lines. Rendered as an **"AI Analysis"** card on the failed deployment detail page. Optional webhook push (Slack, Teams) with the summary.

**Process builder assistant (UI):** Step suggester proposes a starter process from package contents (detects ASP.NET, Windows Service, static site, etc.). Inline script editor sidebar helps write PowerShell/Bash steps — explains available variables, suggests error handling, flags risky patterns. Step configuration helper provides contextual field explanations and smart defaults based on project and target context.

### M12 additinal polish 
OpenTelemetry export to Grafana stack or Seq.
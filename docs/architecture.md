# KrakenDeploy — System Architecture

> Living document. Updated as milestones land; pair with [TASKS.md](../TASKS.md) for the roadmap.

## Project status: pre-production, breaking changes ALLOWED

**KrakenDeploy is not yet deployed to any production installation** (LAUS or otherwise). It runs only in dev / test environments. While that holds:

- **Breaking changes to wire contracts, EF schemas, REST endpoints, step-type names, package IDs, and persisted JSON shapes are permitted without back-compat shims.** Prefer the clean rename / clean redesign over a back-compat alias every time the trade-off comes up.
- **EF migrations may freely drop or rewrite columns** — there's no production data to preserve. A migration that destroys existing data is acceptable; a migration that silently leaves stale shapes in the model is not.
- **No "soft-fallback for old data" branches** in services or workers. If a model invariant says a column must be populated, the runtime should throw when it isn't, not paper over the gap with a warning + legacy code path. Two paths is a maintenance tax that buys nothing while the only data is what dev seeds.
- **API versioning is NOT a constraint yet.** The REST surface, gRPC `.proto` messages, and SignalR hub contracts can be reshaped at will. When we ship to a real installation we'll cut a v1 line.

This policy ends the moment KrakenDeploy is installed on a real customer environment. At that point: contracts freeze, migrations become forward-only with explicit data preservation, and back-compat becomes a design constraint. **Until that moment: prefer correctness now over flexibility later.**

The "B: hard error, no fallback" choice for `Release.VariableSnapshotUpdatedUtc IS NULL` (see Release variable snapshot below) is one application of this policy. So is the `Octopus.FileTransform` → `Octopus.JsonConfigurationVariables` rename (D-8.3) with no alias.

## Topology

Three long-lived processes plus PostgreSQL:

```
┌───────────────────────────────┐                    ┌─────────────────────────┐
│  KrakenDeploy.Server          │                    │  KrakenDeploy.Agent     │
│  (Blazor + REST + SignalR     │ ── SignalR ─────►  │  (Worker service on     │
│   + gRPC + Hangfire)          │ ◄── gRPC stream ── │   the deployment        │
│                               │                    │   target machine)       │
└──────────────┬────────────────┘                    └────────────┬────────────┘
               │ EF Core                                          │ pwsh / bash /
               ▼                                                  ▼ dotnet-script / …
       ┌───────────────┐                                   ┌──────────────────┐
       │  PostgreSQL   │                                   │  deployment      │
       │  (jsonb-heavy)│                                   │  target FS / IIS │
       └───────────────┘                                   └──────────────────┘
```

Agents always dial **out** to the server. No inbound firewall hole at customer sites. SignalR carries control (heartbeats, commands, log lines, step results). gRPC bidirectional streams carry binary payloads (package files, artifact uploads) with backpressure and resume.

## Project layout

| Project | Role |
|---|---|
| `KrakenDeploy.Server` | Blazor UI + REST endpoints + SignalR hubs + Hangfire host. The composition root. |
| `KrakenDeploy.Server.Core` | Domain entities. No infrastructure references. Pure C# types describing the world (Project, Release, Deployment, StepTemplate, …). |
| `KrakenDeploy.Server.Data` | EF Core `KrakenDbContext`, migrations, services (`ReleaseService`, `VariableService`, `OctopusSystemVariablesBuilder`, `BuiltInStepTemplateSeeder`, …). |
| `KrakenDeploy.Server.Transport` | SignalR `AgentHub`, gRPC services, background dispatch workers (`DeploymentWorker`, `RunbookRunWorker`). |
| `KrakenDeploy.Agent` | Worker service running on the deployment target. Hosts `DeploymentExecutor`, `IStepHandler` implementations, `ScriptRunner`. |
| `KrakenDeploy.Agent.Transport` | `IServerLink` implementations (SignalR reverse-tunnel, Direct, Polling). |
| `KrakenDeploy.Contracts` | Shared DTOs, hub interfaces, `.proto` files, step-config key constants (`KrakenIisConfigKeys`, `KrakenScriptConfigKeys`). Referenced by both server and agent. |

## Deployment lifecycle

1. **Release creation** snapshots a project's current deployment process (`Project.Process.Steps`) into `Release.ProcessSnapshot` plus pinned package versions per step. Historical deployments stay reproducible even if the project is edited later.
2. **Deployment scheduled** — a `Deployment` row points at `(Release, Environment, Target?, Tenant?)`. Status starts at `Queued`.
3. **`DeploymentWorker.DispatchAsync`** picks it up (Hangfire-triggered or scheduled). For online targets:
   - Loads the deployment + release + project + environment + target + tenant via EF.
   - **Resolves variables** via `VariableService.ResolveAsync` (tenant variables + project variables + environment-scoped overrides + role-scoped overrides).
   - **Builds the system variable dictionary** via `OctopusSystemVariablesBuilder.BuildForDeployment(...)` — see [variable pipeline](#variable-pipeline) below.
   - **Substitutes** step `Config` values through Octostache using the combined dictionary.
   - Packages everything into a `DeploymentPlan` DTO and sends it to the agent via SignalR (`AgentHub.RunDeploymentAsync`).
4. **Offline-drop targets** go through `DispatchOfflineDropAsync` instead — the plan is materialised into a zip bundle (`DropBundleService`) with scripts, packages, variables, and a `deploy.ps1`/`deploy.sh` orchestrator. The target operator runs the bundle, then returns a signed result bundle.
5. **`DeploymentExecutor` on the agent** receives the plan, runs each `DeploymentStepPlan` through the matching `IStepHandler`, streams log lines + status back over SignalR, and uploads artifacts via gRPC after each step.

`RunbookRunWorker` is the parallel path for ad-hoc runbook execution — same shape, different originating entity (`RunbookRun` instead of `Deployment`), no `Release` context.

## Step execution model

`IStepHandler` is the agent-side extension point:

```csharp
public interface IStepHandler
{
    bool CanHandle(string stepType);
    bool RequiresPackage { get; }
    Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct);
}
```

There are **no in-DI handlers** — Phase D-8.9 removed the last of them, and
step packages are the only execution path. Every step carries a
`(StepPackageName, StepPackageVersion)` pin resolved at author time; the
agent's `DeploymentExecutor.ResolveHandlerAsync` downloads the pinned
`.kdeploy-step` archive (gRPC, signature-verified, cached under
`step-packages-cache/`), loads its executor in a collectible
`AssemblyLoadContext`, and activates the handler whose `CanHandle` claims the
step's type. A step whose type nothing serves is refused **before dispatch**
by `StepTypeExecutionGuard` (SC4-b) with an actionable reason — it never
reaches an agent.

The built-in handlers live as ordinary step packages under `steps/`
(17 projects, ~55 step types — script, IIS, package deploy, Windows service,
Docker, Kubernetes, AWS, Azure, Java, Terraform, misc, package runner). What
each installed package serves is recorded in the **step-type registry**
(`step_types`, maintained by `StepTypeRegistry` on install/uninstall/boot),
which also drives the Add-Step picker and wave routing:
`Octopus.Manual` declares `executionLocus: "server"` in its manifest (a
manual-intervention gate is task-global — see
`docs/design-manual-intervention.md`), `Octopus.DeployRelease` is a System
registry row, and `WavePartitioner` classifies waves from the registry's
locus data — there is no hardcoded server-side type list anymore.

### `ScriptRunner` dispatch

`ScriptRunner.RunAsync(scriptBody, syntax, workDir, envVars, onOutput, ct, powerShellEdition)` writes the script to a temp file with the right extension and shells out:

| Syntax | Edition | Command |
|---|---|---|
| `PowerShell` | `Core` (default) | `pwsh -NonInteractive -NoProfile -File <file>.ps1` |
| `PowerShell` | `Desktop` (Windows) | `powershell.exe -NonInteractive -NoProfile -ExecutionPolicy Bypass -File <file>.ps1` |
| `PowerShell` | `Desktop` (non-Windows) | Falls back to `pwsh` (Windows PowerShell 5.x is Windows-only). |
| `Bash` | — | `bash <file>.sh` |
| `CSharp` | — | `dotnet script <file>.csx` — requires `dotnet tool install -g dotnet-script`. |
| `FSharp` | — | `dotnet fsi <file>.fsx` |
| `Python` | — | `python <file>.py` |

stdout / stderr stream line-by-line into `onOutput(level, line)`, which the executor forwards via `IServerLink.AppendLogAsync` to the server in real time.

### Clean-room policy

Built-in step types that mirror Octopus's (`Octopus.IIS`, `Octopus.TentaclePackage`, etc.) are implemented **without reference to decompiled Calamari source**. The behavioural contract is derived from public Octopus docs and observable inputs (exported deployment-process JSON, real `Octopus.Action.*` parameter shapes). This keeps the IP question clean regardless of Calamari's licence and lets us choose a PowerShell-template implementation rather than copying Calamari's C# command/handler structure.

## Variable pipeline

Three layers compose the variables a script sees:

1. **User-defined variables** — resolved server-side by `VariableService.ResolveAsync(projectId, envId, targetId, roles, tenantId)`. Merges project variables, environment-scoped overrides, role-scoped overrides, and tenant variables. Returns a flat `Dictionary<string,string>`.
2. **Octopus system variables** — produced by `OctopusSystemVariablesBuilder.BuildForDeployment(...)` (or `BuildForRunbookRun(...)`). ~70 keys grouped by scope: Deployment.\*, Project.\*, Release.\*, Environment.\*, Tenant.\*, Machine.\*, indexed per-step Action[StepName].\* and Step[StepName].\*, Web.\*, Time.\*, plus deferred placeholders for step packs not yet implemented (Azure.\*, Aws.\*, Kubernetes.\*) emitted as empty strings with `// TODO(kraken-equivalent)` comments.
3. **Octostache substitution** — both dicts merge into a single `VariableDictionary`. Each step's `Config` values are expanded through `varDict.Evaluate(value)` before the plan is sent. `#{MyVar}`, `#{each x in Items}`, `#{Var | join ", "}` etc. all resolve here.

The fully-substituted plan ships to the agent. The agent layers in one more set:

4. **Current-step un-indexed keys** — `ScriptStepHandler` adds `Octopus.Action.Name`, `Octopus.Action.Id`, `Octopus.Action.Number`, `Octopus.Step.Name`, `Octopus.Step.Number`, and the `Octopus.Action.Package.*` trio for the step currently running, merged into both env vars and (for PowerShell) the `$OctopusParameters` preamble.

Script-visible surface ends up (per language):

- **PowerShell**: `$OctopusParameters["Octopus.Project.Name"]`, `#{Octopus.Project.Name}` (resolved server-side), plus `Write-KrakenInfo`/`Write-KrakenWarning`/`Write-KrakenError`, `Register-KrakenArtifact`, and Octopus-compatible aliases `Set-OctopusVariable` (emits `##octopus[setVariable …]`) and `New-OctopusArtifact`.
- **Bash**: env vars are flattened (`Octopus.Project.Name` is set as-is — dots are fine in env names but not in bash identifiers, so the preamble exposes `get_octopusvariable`, `set_octopusvariable`, `new_octopusartifact` helpers).
- **C# / F# (dotnet-script / dotnet fsi)**: `OctopusParameters` dict (filtered to keys starting with `Octopus.`), `GetOctopusVariable`, `SetOctopusVariable`, `NewOctopusArtifact` (camelCase in F#: `getOctopusVariable`, `setOctopusVariable`, `newOctopusArtifact`).
- **Python**: `octopusvariables` (and `OctopusParameters` alias) dict, `get_octopusvariable`, `set_octopusvariable`, `new_octopusartifact`.

All language helpers ultimately call the same back-end: env-var reads for inputs, base64-encoded `##octopus[setVariable]` stdout markers for output-variable capture (parsed agent-side by `OctopusMessageParser`), and the `KRAKEN_ARTIFACTS_PATH` directory for artifact registration (picked up by the post-step `Directory.GetFiles` scan).

### Output variables

Scripts use `Set-OctopusVariable -name X -value Y` (or emit a raw `##octopus[setVariable name='base64' value='base64']` stdout marker from any language). The agent's `DeploymentExecutor` wraps each step's log callback with `OctopusMessageParser.TryParse(line)` and:

1. **`SetVariableMessage`** — captured value is routed into a per-step `Dictionary<string,string>`. The marker line itself is suppressed from the user-visible log.
2. After the step completes, captured outputs are reported to the server via `IServerLink.ReportStepOutputVariablesAsync` → `AgentHub.ReportStepOutputVariablesAsync` → upsert into `deployment_output_variables` (PK by `(DeploymentId, StepName, Name)`).
3. The executor merges every prior step's outputs into the *next* step's `Plan.Variables` as `Octopus.Action[StepName].Output.X`, using `DeploymentPlan with { Variables = merged }`. Subsequent scripts read them via `$OctopusParameters["Octopus.Action[StepFoo].Output.Bar"]` or `#{Octopus.Action[StepFoo].Output.Bar}` (server-side resolution kicks in when a release runs a process containing variable expressions that bind to these keys).

Other markers handled by the parser:

- `##octopus[stdout-warning|error|default]` — sticky log level for subsequent lines.
- `##octopus[createArtifact …]` — surfaced as an info-level log line (the actual artifact upload still flows through the existing artifacts-dir scan).
- `##octopus[progress percentage='X' message='…']` — surfaced as `[Progress X%] …` info line.
- Unknown commands log a debug message and pass the original line through as a normal log line.

## Step templates

`StepTemplate` is a reusable definition of a step: an `ActionType` (e.g. `Kraken.Script`), a `Properties` dict that's copied onto a `DeploymentStep.Config` when applied, and a list of `Parameters` that drive the UI form. Extra metadata fields (`Category`, `Author`, `Website`, `LogoUrl`, `Source`) drive the picker / filter UI.

Since the step-systems consolidation (SC2/SD-8), a template is a **preset**:
a pre-filled form over a step type that an installed package serves. Presets
resolve *below* packages — a preset whose base type nothing serves is
importable but flagged unrunnable in the picker and refused at claim time,
healing when the serving package installs.

Sources tracked by the `StepTemplateSource` enum:

- **`BuiltIn`** — RETIRED (SC2). Built-in picker cards derive from the
  step-type registry now; the seeder is deleted and the migration removed
  its rows. The enum value survives only for historical data.
- **`CommunityLibrary`** — JSON files from the configured template feeds (see below). Parsed by `OctopusLibraryImporter.Parse`; imported via `StepTemplateService.ImportFromJsonAsync(..., source: StepTemplateSource.CommunityLibrary)`. Upserted by Octopus `CommunityActionTemplateId` so re-import updates in place.
- **`LocalImport`** — same parser path but the entry point is a single-file paste, single-file picker, or the bulk "Import from folder" feature pointed at a clone of the Library repo.
- **`UserAuthored`** — created via `CreateStepTemplateDialog`.

### Categories

Each template carries the small-bucket `Category` from the source JSON (e.g. `aws`, `iis`, `windows-iis`). The UI groups templates by the **big-bucket** display category derived via `KrakenDeploy.Contracts.Steps.StepTemplateCategoryMap.GetBigBucket(small)`. The mapping table is embedded as `category-mapping.json` inside `KrakenDeploy.Contracts.dll`; it covers ~80 small buckets across 11 big buckets ("Development and Scripting", "Containers and Orchestration", "Cloud Native Services", "Infrastructure as Code", "Server Environments", "Configuration Management", "Source Control", "Notifications", "Reporting and Telemetry", "Security and Compliance", "Workflow"). Anything unmapped falls into `Other`.

### Community catalog

`StepTemplateCatalogEntry` rows in `step_template_catalog` mirror metadata for every template JSON in the configured **feeds** (SC6 — multi-feed: `StepTemplates:Catalog:Feeds`, defaulting to `OctopusDeploy/Library@master` plus the Kraken community repo `DomagojJugovich/kraken-steps@main`, both under `step-templates/`). `StepTemplateCatalogService.RefreshAsync(ct)` keeps them in sync per feed:

1. One GitHub **Git Trees API** call per feed (`GET /repos/{owner}/{repo}/git/trees/{branch}?recursive=1`) returns every blob's path + SHA in one shot — cheap on the 60-req/hr unauthenticated limit.
2. For each `{subdir}/*.json` whose **per-file SHA has changed** since the last sync, fetch the raw file via `raw.githubusercontent.com/...` (no API limit) and re-parse metadata.
3. Upsert by `(FeedKey, PathInRepo)`; orphan deletion is **scoped to the feed that synced** — one feed's outage never deletes another feed's rows, and never aborts its sync (the refresh throws only when every feed failed). `CommunityActionTemplateId` stays globally unique; a duplicate arriving from a second feed is skipped with a warning.

Refresh strategy:
- **Hangfire recurring job** `kraken.step-template-catalog-poll` runs `Cron.Hourly()`, gated on the `feeds.step-template-catalog` feature flag (runtime kill-switch; `StepTemplates:Catalog:Enabled=false` is the deployment-posture switch). Network failures log a warning, record the error in the `StepFeedHealthDocument` settings document (surfaced in the picker's feed-health strip and the community page), and roll over to the next tick.
- **Manual** refresh from the `/step-templates/community` page via `POST /api/step-template-catalog/refresh` (permission `StepTemplateCreate`).

The named `HttpClient` `kraken.github` is registered in `Program.cs` with the mandatory GitHub `User-Agent`. Set `GitHub:Token` in configuration to bump the rate limit from 60 to 5000 req/hour (the per-file fetches go via `raw.githubusercontent.com` which doesn't count regardless).

Installing a catalog row → `StepTemplateCatalogService.InstallAsync(id)` fetches the full JSON via `DownloadUrl` and routes through `StepTemplateService.ImportFromJsonAsync(json, source: CommunityLibrary)`.

### Add-Step picker

When a user clicks "Add Step", `ChooseStepTemplateDialog` shows the SC5 source-sectioned picker. Left pane = Featured / Installed / Presets / Community + each big-bucket category from `StepTemplateCategoryMap`, plus search. Right side = sections in order:

- **Featured** — registry types flagged `featured` in their package manifest (plus the Step Group System row).
- **Installed step types** — one card per `step_types` registry row, i.e. exactly what installed packages serve. Cards carry the serving `package version` badge.
- **Presets** — installed `StepTemplate` rows (community/user). A preset whose base type nothing serves gets a "requires X — not installed" badge with a disabled Add.
- **Available to install** — uninstalled community catalog entries; "Install and Add" installs via the catalog service first, then proceeds as if it had been installed all along.

A feed-health strip at the bottom shows last sync + last error for both catalog feeds. The dialog returns a `ChooseStepTemplateResult`; hosts (`Process.razor`, `RunbookDetail.razor`) open the single unified `StepFormDialog` either by step type (`ActionType = result.StepTypeId` — schema resolved pin-aware via `StepSchemaResolver`: pinned version's schema → serving package's newest with a provenance notice → preset parameters via `StepTemplateSchemaAdapter`) or with the picked preset (`Template` — parameter form; a parameterless preset falls through to the resolver). Editing an existing step opens the same dialog with the `Step`, resolving pin-aware the same way. (`TemplatedStepFormDialog` was deleted back in Phase C's `afed0d9`; `StepTemplateSchemaAdapter` maps each `ControlType` — `SingleLineText`, `MultiLineText`, `Sensitive`, `Checkbox`, `Select`, `Package` — onto the schema renderer's widgets.)

### Server-side execution

A step's `Config["Octopus.Action.RunOnServer"]` flag (set via the radio group in both step forms) determines whether the step runs on the agent or in the server process:

- **`false` (default)** — step is included in the plan dispatched to the agent over SignalR and runs via the agent's `IStepHandler` chain (see [Step execution model](#step-execution-model)).
- **`true`** — step is held back at the server and executed in-process by `ServerScriptStepRunner` (in `KrakenDeploy.Server.Transport`). The runner mirrors the agent's `ScriptRunner` for syntax dispatch (PowerShell Desktop/Core, Bash, CSharp via `dotnet script`, FSharp via `dotnet fsi`, Python) and writes log entries directly to `deployment_log_entries`, broadcasting over `UiHub` so the live-log UI surface is identical to the agent path.

`DeploymentWorker` partitions the plan's steps into consecutive same-side groups (`PartitionIntoGroups`) and walks them in declared order:

- **Server group** — run each step in-process via `ServerScriptStepRunner`. Honours "Server on behalf of each deployment target" via the role filter `StepAppliesToTarget(deployment, step)` — a server step with `TargetRoles` only runs when the deployment's target has at least one matching role.
- **Target group** — dispatch a sub-plan (`plan with { Steps = group.Steps }`) to the agent and **await** its completion before moving to the next group. The wait is coordinated by `IPendingSubPlanRegistry`, a singleton holding one `TaskCompletionSource<SubPlanResult>` per deployment ID. When the agent's `CompleteDeploymentAsync` arrives, `AgentHub` checks the registry first: if a TCS is pending the hub resolves it and returns immediately (the worker resumes); otherwise it falls through to the existing finalize-the-deployment logic for single-shot deployments. This lets any order — `target → server → target → server` — run correctly with multiple round trips.

Fully-server-side deployments complete without ever sending a plan to the agent (and so don't require an online agent). After all groups succeed, the worker writes `Succeeded` to the deployment row. A failed sub-plan or a failed server step short-circuits the loop and writes `Failed` with the underlying error.

The PowerShell preamble used server-side mirrors the agent's: `$OctopusParameters` is pre-populated, plus `Set-OctopusVariable` / `Write-KrakenInfo` / `Get-KrakenVariable` helpers. Output-variable capture via the `##octopus[setVariable]` stdout marker is _not yet_ wired through on the server side (the agent path handles it via `OctopusMessageParser` in `DeploymentExecutor`); follow-up work would extract that into a shared utility and apply it here too.

### Referenced packages

A step can declare extra packages alongside its primary one — useful for steps that need bundled tooling (a helper module, `jq`, a Terraform binary, etc.). Each declared `PackageReference` (defined in `KrakenDeploy.Contracts.Steps`) carries a friendly `Name`, the feed's `PackageId`, an optional `Version` (blank = latest at dispatch time), and an `Extract` bool. The list is stored as a JSON-encoded array in step config under `Octopus.Action.Package.PackageReferences` (the Octopus-compatible key, exposed as `KrakenScriptConfigKeys.PackageReferences`).

Flow:

1. **Server (plan build)** — `PackageReferenceResolver.ResolveAsync` parses the JSON, looks up the latest version for any entry missing one (via `db.Packages.Where(p => p.PackageId == id).OrderByDescending(p => p.UploadedUtc)`), and writes the resolved list onto the `DeploymentStepPlan.ReferencedPackages` field (a new nullable, backward-compatible record member). Used by both `DeploymentWorker` and `RunbookRunWorker`.
2. **Agent (execution)** — `DeploymentExecutor.ExecuteStepAsync` downloads each referenced package via the existing `GrpcPackageDownloader`. With `Extract = true` (the default) the zip is unpacked to `{tempRoot}/extracted/refs/<sanitised-name>/`; otherwise the zip path itself is exposed. Resolved paths land in `StepHandlerContext.ReferencedPackagePaths` keyed by friendly name.
3. **Script surface** — `ScriptStepHandler` exposes two accessors per reference:
   - `$OctopusParameters["Octopus.Action.Package[<Name>].ExtractedPath"]` / `#{Octopus.Action.Package[<Name>].ExtractedPath}` (also an env var of the same name)
   - `OCTOPUS_REFERENCED_PACKAGE_<NAME>_PATH` env var (Octopus's flat-name convention)

UI: `StepFormDialog` (script form) has a "Referenced Packages" inline grid. Other step forms inherit the underlying machinery — they simply persist a `Octopus.Action.Package.PackageReferences` JSON value through their existing Config dict.

Reproducibility: `ReleaseService.CreateAsync` calls `PinReferencedPackagesAsync` per step when building the `ProcessSnapshot`, pinning any unpinned referenced packages to the latest uploaded version (strict — throws if no version exists, same as the primary `PackageVersion`). The deploy-time `PackageReferenceResolver` then sees pre-pinned entries and passes them through unchanged. Every deploy of a release runs with the exact same set of referenced packages.

## Extension points

| To add | Where |
|---|---|
| A new step type | Author a **step package** — in-tree: a new `steps/KrakenDeploy.Steps.*` project (KrakenStepType items + `ui-schemas/{typeId}.json`, referenced from `KrakenDeploy.Server.csproj`'s seed list); externally: `dotnet new krakenstep` against Kraken.SDK. See `docs/step-packages.md`. Install writes the registry row that makes it pickable — nothing else to seed. |
| A new Octopus system variable | Add a line to the right section in `OctopusSystemVariablesBuilder`. If the value isn't yet available, emit empty string with a `// TODO(kraken-equivalent)` comment so the gap is grep-auditable. |
| A new step config key | Add a constant to the matching `Kraken<X>ConfigKeys` static class in `KrakenDeploy.Contracts/Steps/`. Keep names Octopus-compatible (`Octopus.Action.*`) when there's a sensible existing name to mirror. |
| A new agent transport | Implement `IServerLink` in `KrakenDeploy.Agent.Transport`. The only live transport is `SignalRServerLink` (agent-initiated reverse tunnel; the server pushes work back over the same full-duplex connection). Air-gapped targets use the agentless OfflineDrop path instead. |
| A new background job | Add to Hangfire setup in `Program.cs` (`RecurringJob.AddOrUpdate(...)`). Existing jobs in `KrakenDeploy.Server/Services/RecurringJobs/`. |

## Step composition — child steps + ForEach (M15)

A deployment process is a **tree** at design time and a **flat list** at runtime. The design-time tree lets a single Step Group own multiple children, and a ForEach group expand into one iteration per array-variable item. The runtime sees a flat `DeploymentStepPlan[]` exactly as before; the M14.4 wave partitioner, Run Condition gates, Required gates, Retries, and Timeouts all operate on the flat list unchanged.

### One marker step type

`Kraken.StepGroup` is the only step type that can have children. Leaf step types (`Kraken.Script`, `Kraken.IIS`, …) cannot — validation in `ProcessService.ValidateAsync` refuses non-empty `Children` on a leaf-typed step. A Step Group's behaviour is driven by its **Config bag**, not its type:

| Config key | Mode | Notes |
|---|---|---|
| `Octopus.Action.ForEach.Collection` set | ForEach loop | Children re-emitted once per array-variable item. |
| `Octopus.Action.MaxParallelism` set | Rolling deployment | Reserved for M-RollingDeployments. M15 preserves the value but treats the group as a plain container. |
| Neither set | Plain container | Children run sequentially in `SortOrder`; per-child `StartTrigger = StartWithPrevious` opts a child into parallel-with-previous through M14.4's wave partitioner. |

A Step Group must NOT carry leaf-only Config keys (`Octopus.Action.Script.ScriptBody`, package selectors, IIS / Windows Service / Substitute / Manual keys, etc.). The catalogue lives in `KrakenStepTypes.LeafOnlyConfigKeys` and is referenced by both the validator and the importer.

### `DeploymentPlanFlattener` — tree at design-time, flat at runtime

`DeploymentPlanFlattener.Flatten(snapshotSteps, arrayVars, scalarVars)` runs at deployment dispatch (before M14.4's wave partitioner). Pure-function — no DB / no IO. Returns `(Plans, SnapshotByPlanIndex, Warnings)`:

- `Plans[]` — flat `DeploymentStepPlan[]` ready for the wave partitioner.
- `SnapshotByPlanIndex[]` — maps each emitted plan back to the snapshot it was derived from. The orchestrator reads it everywhere it used to index a flat snapshot array directly. Multiple ForEach plans can share a snapshot.
- `Warnings[]` — the orchestrator translates each into an audit + log line + Required-gate decision.

Octostache substitution moves into the flattener (replaces `DeploymentWorker.SubstituteConfig`) so per-iteration variable values resolve correctly — `#{item}` in a child's `ScriptBody` reads the current iteration's value, not "always the last item."

### Synthetic naming for ForEach iterations

| | Form | Use |
|---|---|---|
| `DeploymentStepPlan.AccumulatorKey` | `OriginalName[index]` (always, e.g. `Deploy[0]`, `Deploy[1]`) | Internal key for output-variable reporting + Octostache cross-iteration references. Agent reports outputs against this key. |
| `DeploymentStepPlan.Name` | `OriginalName [var=value]` (clean) / `OriginalName [var=#index]` (fallback) | Display name on the deployment log, the Steps tab, and audit details. |

"Clean" = `value` ≤ 40 chars, no newlines / tabs / `]`. Operators reading logs identify which iteration is running at a glance; the fallback form keeps long / weird values from breaking layout.

### Cross-iteration output access

```text
#{Octopus.Action[Deploy[0]].Output.X}        — synthetic-key form (M15 documented)
#{Octopus.Action[Deploy [item=staging]].Output.X}  — display-name form (works incidentally)
```

The synthetic key is the supported form; the display-name form happens to work because the synthetic name *is* a step name in the plan, but the long form makes templates ugly.

### Nested ForEach

Allowed. Inner iteration variable shadows the outer (`#{item}` always refers to the innermost). If the outer ForEach used a distinct `IterationVariable` like `env`, `#{env}` stays accessible inside the inner loop.

Inner ForEach's `Collection` can reference the outer iteration variable (e.g. `Collection = "#{env}-instances"`); the flattener resolves the inner collection **lazily** per outer iteration so each outer pass sees the right inner collection.

### Parallel ForEach

`Octopus.Action.ForEach.Parallel = "true"` on the Step Group makes iterations siblings in the same M14.4 wave. The flattener emits the first child of iterations 1..N with `StartTrigger = StartWithPrevious` so the wave partitioner groups them together. M14.4's last-writer-wins collision rule + audit applies — synthetic naming means cross-iteration collisions are rare by construction (each iteration's outputs live under a distinct accumulator key), but explicit same-name `Set-OctopusVariable` calls across iterations still surface as `Deployment.ParallelOutputCollision` warnings.

### Empty / undefined collections

| | Effect | Audit |
|---|---|---|
| Empty collection (`envs = []`) | Group emits zero plans — operators see a no-op in the Steps tab. | `Deployment.ForEachEmpty` |
| Undefined collection (variable doesn't exist) | Group emits zero plans + a `ForEachUnresolved` warning. The orchestrator applies the group's `Required` flag — Required → abort the deployment, non-required → continue with `hasFailed`. | `Deployment.ForEachUnresolved` |

### Validation rules (`ProcessValidator`)

| Rule | Code |
|---|---|
| Cycle freedom (a step cannot be its own ancestor). DFS over the `ParentStepId` chain. | `Cycle` |
| Parent locality (`ParentStepId` references a step in the same process). | `UnknownParent` |
| Group-only parenthood (only `Kraken.StepGroup`-typed steps may have children). | `LeafTypeHasChildren` |
| Leaf-config exclusion (a Step Group must NOT carry leaf-only Config keys). | `GroupHasLeafConfig` |

The validator accumulates every error in one pass so the editor can surface them all at once. Called by the editor before save AND by the flattener as defence in depth (corrupted data fails the deployment with a clear message rather than throwing mid-walk).

### Octopus import (multi-action steps)

Pre-M15, the importer skipped any Octopus step with `Actions.Count > 1` with a "parallel actions not yet supported" warning — silently dropping real process structure. M15.1 imports the same structure honestly as a Step Group:

- Parent: `StepType = "Kraken.StepGroup"`, inherits step-level `TargetRoles` + step-level Octopus properties verbatim (including `Octopus.Action.MaxParallelism` for the future M-RollingDeployments milestone).
- Children: one per Octopus action. Children 2..N get `StartTrigger = StartWithPrevious` because Octopus's default for multi-action steps is parallel-on-same-target. Operators wanting sequential children flip them to `StartAfterPrevious` in the editor after import.

An import-time warning explains the choice: *"Step 'X' has N actions; imported as a Step Group with children running in parallel (StartTrigger=StartWithPrevious). Change children to StartAfterPrevious for sequential execution."*

## Spaces and tenancy

`ISpaceScoped` is a marker interface; every top-level aggregate carries `SpaceId`. `KrakenDbContext` applies a global query filter so all reads are auto-scoped to the current Space (resolved from the `kraken-active-space` cookie via `HttpSpaceContext`). Multi-space is supported in code but invisible in the UI when only the Default Space exists. Tenants are project-level — a project lists its tenants, deployments can be tenant-scoped (`Deployment.TenantId`), and tenant variables compose into the resolved variable set per deployment.

## Data model — jsonb-heavy

Postgres `jsonb` columns are used liberally for shapes that change with step types, parameter sets, audit snapshots, etc.:

- `step_templates.properties`, `step_templates.parameters`
- `deployment_steps.config`, `releases.process_snapshot`, `runbook_runs.process_snapshot`
- `audit_entries.before_state`, `audit_entries.after_state`
- `variables.value` (string-array vars stored as JSON strings)

This keeps the schema stable while letting per-step-type data evolve freely. Indexes are added only where there's a known query pattern.

---

For roadmap, see [TASKS.md](../TASKS.md). For deployment, see [docs/on-prem-guide.md](on-prem-guide.md). For HA, see [docs/ha-pair.md](ha-pair.md).

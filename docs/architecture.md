# KrakenDeploy — System Architecture

> Living document. Updated as milestones land; pair with [TASKS.md](../TASKS.md) for the roadmap.

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

Handlers are registered in DI order (first match wins). `DeploymentExecutor` calls `_handlers.FirstOrDefault(h => h.CanHandle(step.StepType))`.

Current handlers:

| Handler | Step types | Notes |
|---|---|---|
| `ScriptStepHandler` | `Kraken.Script`, `Octopus.Script` | Inline script in PowerShell / Bash / CSharp / FSharp / Python. Single entry point. |
| `KrakenIisStepHandler` | `Kraken.IIS`, `Octopus.IIS` | Generates a PowerShell deployment script, runs via `ScriptRunner`. |
| `SubstituteVariablesStepHandler` | `Octopus.SubstituteVariables` | In-place file variable substitution. |
| `JsonConfigurationVariablesStepHandler` (step package `octopus.jsonconfigurationvariables`) | `Octopus.JsonConfigurationVariables` | JSON config variable substitution by dotted path (mirrors Octopus's "JSON Configuration Variables" feature). XDT for XML lives on `Octopus.TentaclePackage`, not here. |
| `ManualInterventionStepHandler` | `Octopus.Manual` | Auto-approves in unattended mode. |

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

Four sources tracked by the `StepTemplateSource` enum:

- **`BuiltIn`** — seeded at startup by `BuiltInStepTemplateSeeder` (idempotent by name). Currently: `Kraken.IIS — Deploy Web Site`, `Kraken.Script — Run a Script`. These rows are auto-managed; the seeder updates them on every startup if their definition has drifted.
- **`CommunityLibrary`** — JSON files from `https://github.com/OctopusDeploy/Library/tree/master/step-templates`. Parsed by `OctopusLibraryImporter.Parse`; imported via `StepTemplateService.ImportFromJsonAsync(..., source: StepTemplateSource.CommunityLibrary)`. Upserted by Octopus `CommunityActionTemplateId` so re-import updates in place.
- **`LocalImport`** — same parser path but the entry point is a single-file paste, single-file picker, or the bulk "Import from folder" feature pointed at a clone of the Library repo.
- **`UserAuthored`** — created via `CreateStepTemplateDialog`.

### Categories

Each template carries the small-bucket `Category` from the source JSON (e.g. `aws`, `iis`, `windows-iis`). The UI groups templates by the **big-bucket** display category derived via `KrakenDeploy.Contracts.Steps.StepTemplateCategoryMap.GetBigBucket(small)`. The mapping table is embedded as `category-mapping.json` inside `KrakenDeploy.Contracts.dll`; it covers ~80 small buckets across 11 big buckets ("Development and Scripting", "Containers and Orchestration", "Cloud Native Services", "Infrastructure as Code", "Server Environments", "Configuration Management", "Source Control", "Notifications", "Reporting and Telemetry", "Security and Compliance", "Workflow"). Anything unmapped falls into `Other`.

### Community catalog

`StepTemplateCatalogEntry` rows in `step_template_catalog` mirror metadata for every step-template JSON in `https://github.com/OctopusDeploy/Library/tree/master/step-templates`. `StepTemplateCatalogService.RefreshAsync(ct)` keeps them in sync:

1. One GitHub **Git Trees API** call (`GET /repos/OctopusDeploy/Library/git/trees/master?recursive=1`) returns every blob's path + SHA in one shot — cheap on the 60-req/hr unauthenticated limit.
2. For each `step-templates/*.json` whose **per-file SHA has changed** since the last sync, fetch the raw file via `raw.githubusercontent.com/...` (no API limit) and re-parse metadata.
3. Upsert by `CommunityActionTemplateId`. Orphans (paths removed upstream) are deleted.

Refresh strategy:
- **Hangfire recurring job** `kraken.step-template-catalog-poll` runs `Cron.Hourly()`. Network failures log a warning and roll over to the next tick rather than retrying (Hangfire would otherwise retry on a tight backoff).
- **Manual** refresh from the `/step-templates/community` page via `POST /api/step-template-catalog/refresh` (permission `StepTemplateCreate`).

The named `HttpClient` `kraken.github` is registered in `Program.cs` with the mandatory GitHub `User-Agent`. Set `GitHub:Token` in configuration to bump the rate limit from 60 to 5000 req/hour (the per-file fetches go via `raw.githubusercontent.com` which doesn't count regardless).

Installing a catalog row → `StepTemplateCatalogService.InstallAsync(id)` fetches the full JSON via `DownloadUrl` and routes through `StepTemplateService.ImportFromJsonAsync(json, source: CommunityLibrary)`.

### Add-Step picker

When a user clicks "Add Step" on a project's Process page, `ChooseStepTemplateDialog` shows the unified Octopus-style "Choose Step Template" screen. Left pane = Featured / Installed / each big-bucket category from `StepTemplateCategoryMap`, plus search. Right grid = a permanent "Run a Script" sentinel + every installed `StepTemplate` + every uninstalled community catalog entry. Clicking "Install and Add" on a community card installs the template via the catalog service first, then proceeds as if it had been installed all along. The dialog returns a `ChooseStepTemplateResult` so `Process.razor` can route to the right follow-up form:

- **Script sentinel + Script-flavoured templates** (`Kraken.Script` / `Octopus.Script`) → `StepFormDialog` (script-body editor).
- **Other ActionTypes** → `TemplatedStepFormDialog`, a generic form that renders one input per `StepTemplateParameter` based on its `ControlType`:

  | ControlType | Editor |
  |---|---|
  | `SingleLineText` (default) | `RadzenTextBox` |
  | `MultiLineText` | `RadzenTextArea` (6 rows, monospace) |
  | `Sensitive` | `RadzenPassword` |
  | `Checkbox` | `RadzenCheckBox<bool>` with `"true"` / `"false"` round-trip |
  | `Select` | `RadzenDropDown` over `"value\|Label"` options parsed from `SelectOptions` |
  | `Package` | `RadzenTextBox` (full package picker is Phase 8) |

  On Save the form merges template `Properties` (template-author defaults) with the user's parameter values (user values win), and calls `ProcessService.AddStepAsync` with `template.ActionType` or `UpdateStepAsync` for edits. Edit mode also preserves any pre-existing Config keys the template doesn't know about.

Editing an existing step routes the same way — `Process.razor.OpenEditStepAsync` switches on `step.StepType` (script → `StepFormDialog`, otherwise look up a `StepTemplate` whose `ActionType` matches the step and open `TemplatedStepFormDialog`; surface a warning notification if no template matches).

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
| A new step type | Add an `IStepHandler` to `KrakenDeploy.Agent/Deployment/StepHandlers/`. Register in `Program.cs`. Optionally seed a `StepTemplate` from `BuiltInStepTemplateSeeder` so it appears in the UI step picker. |
| A new Octopus system variable | Add a line to the right section in `OctopusSystemVariablesBuilder`. If the value isn't yet available, emit empty string with a `// TODO(kraken-equivalent)` comment so the gap is grep-auditable. |
| A new step config key | Add a constant to the matching `Kraken<X>ConfigKeys` static class in `KrakenDeploy.Contracts/Steps/`. Keep names Octopus-compatible (`Octopus.Action.*`) when there's a sensible existing name to mirror. |
| A new agent transport | Implement `IServerLink` in `KrakenDeploy.Agent.Transport`. Existing impls: `SignalRServerLink` (reverse-tunnel), `DirectServerLink` (LAN), `PollingServerLink` (restricted networks). |
| A new background job | Add to Hangfire setup in `Program.cs` (`RecurringJob.AddOrUpdate(...)`). Existing jobs in `KrakenDeploy.Server/Services/RecurringJobs/`. |

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

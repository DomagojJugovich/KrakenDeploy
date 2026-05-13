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
| `FileTransformStepHandler` | `Octopus.FileTransform` | XML config transforms. |
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

Script-visible surface ends up:

- **PowerShell**: `$OctopusParameters["Octopus.Project.Name"]`, `#{Octopus.Project.Name}` (resolved server-side), plus `Write-KrakenInfo`/`Write-KrakenWarning`/`Write-KrakenError` helpers + `Register-KrakenArtifact` (alias for `New-OctopusArtifact` once Phase 6b lands).
- **Bash / dotnet-script / Python**: same values via environment variables (`OctopusEnvironmentName`, `KrakenDeploymentId`, plus every `Octopus.*` key flattened into the env). Phase 6d adds per-language preambles for parity with `$OctopusParameters`.

## Step templates

`StepTemplate` is a reusable definition of a step: an `ActionType` (e.g. `Kraken.Script`), a `Properties` dict that's copied onto a `DeploymentStep.Config` when applied, and a list of `Parameters` that drive the UI form.

Three sources:

- **Built-in** — seeded at startup by `BuiltInStepTemplateSeeder` (idempotent by name). Currently: `Kraken.IIS — Deploy Web Site`, `Kraken.Script — Run a Script`.
- **Community Library** — JSON files from `https://github.com/OctopusDeploy/Library/tree/master/step-templates`. Parsed by `OctopusLibraryImporter.Parse`; imported via `StepTemplateService.ImportFromJsonAsync`. Upserted by Octopus `CommunityActionTemplateId` so re-import updates in place.
- **User-authored** — created via `CreateStepTemplateDialog`.

Phase 4 (community catalog browser) and Phase 3 (bulk-import from folder) layer on top — see [TASKS.md M10.3](../TASKS.md#m103--octopus-compatibility-deepening--ux-polish).

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

# Execution Engine — Server Tasks, Waves, Targets & Failure Modes

| | |
|---|---|
| **Version** | 1.2 |
| **Date** | 2026-07-18 |
| **Authors** | Domagoj Jugovic, Claude (Fable 5), Claude (Opus 4.8) |
| **Status** | Draft |
| **Technologies** | .NET 10, EF Core 10, PostgreSQL, SignalR, Octostache, Hangfire |
| **Projects** | KrakenDeploy.Server.Transport, KrakenDeploy.Server.Data, KrakenDeploy.Server.Core, KrakenDeploy.Execution, KrakenDeploy.Contracts, KrakenDeploy.Agent |

## Purpose

Reference for how a server task (deployment or runbook run) executes: the
shared data spine, the wave model, server-side vs agent-side steps, rolling
windows, failure modes, and the durability machinery (leases, reconciler,
reconnect, outbox, optimistic concurrency). Also records precisely **where
the deployment/runbook unification currently stops** and the known gaps
that follow from it.

## 1. The spine: one task model, two kinds

Every execution is a row in `server_tasks`, TPH-mapped
(`ServerTaskConfiguration`): abstract `ServerTask` → `Deployment` /
`RunbookRun`, discriminator `Kind` (`ServerTaskKind`), with a CHECK
constraint (`ck_server_tasks_kind_owner`) enforcing `release_id` vs
`runbook_id` per kind. Both kinds share:

- **Children** (all FK `task_id` → base, `ON DELETE CASCADE`):
  `TaskTargetAssignment` (the *sole* authority for the target set;
  `AddedUtc` order makes `targets[0]` the canonical target), `TaskArtifact`,
  `TaskOutputVariable` (sensitive values AES-GCM at rest),
  `TaskStepOutcome`, and the hybrid log pair `TaskLogLiveEntry` (live
  staging) + `TaskStepLog` (compacted per-step blob).
- **One status enum** — `DeploymentStatus` for both kinds; `IsTerminal()`
  (`DeploymentStatusExtensions`) is the single terminal authority
  (`PendingOfflineResult` is deliberately non-terminal).
- **One process model** — `Process` / `ProcessStep` with polymorphic owner
  `(OwnerKind, OwnerId)`: `Project` owns a deployment process, `Runbook`
  owns its own. `ProcessStep` carries the full execution-knob set for both
  kinds: `Condition`, `ConditionVariableExpression`, `Required`,
  `MaxRetries`, `RetryDelaySeconds`, `TimeoutSeconds`, `StartTrigger`, plus
  M15 composition (`ParentStepId` → `Kraken.StepGroup` children).
- **Provenance** — `CreatedByUserId/Display`, `Cause`, `CauseDetail`,
  written only via `TaskInitiator.StampOnto()`.
- **Durability columns** — `ClaimedBy`, `LeaseUntil`, `ScheduledFor`, plus
  a shadow `xmin` row-version token (§7).
- **One hub** — `AgentHub` resolves logs/completions/output variables/step
  outcomes against `db.ServerTasks`, kind-agnostic. The agent itself never
  knows which kind it is running; it just executes a `DeploymentPlan`.

**Snapshot asymmetry.** A deployment executes the frozen
`Release.ProcessSnapshot` + `Release.VariableSnapshot` (frozen at release
creation / "Update Variables"; a null `VariableSnapshotUpdatedUtc` refuses
dispatch). A runbook run freezes `ProcessSnapshot` onto the run itself at
trigger time (`RunbookService.TriggerAsync`) but resolves **variables
live** at dispatch — there is no runbook variable snapshot.

## 2. Lifecycle of a deployment

1. **Create** — `DeploymentService.CreateAsync` is the single creation
   path; even server-side `Octopus.DeployRelease` steps create *child*
   deployments through it (system initiator, `ParentTaskId` self-FK). Row
   inserted `Queued`; a `TenantWorkItem` wake-up goes to an unbounded
   channel. **Channels are at-least-once wake-ups only; the DB row is the
   source of truth.** A future `ScheduledFor` is persisted and *not*
   enqueued (the minutely job dispatches it) — exactly one dispatch path
   per task, no double-dispatch.
2. **Prepare** — `DeploymentWorker.DispatchCoreAsync` resolves the Space
   filter-free, checks the freeze gate, resolves per-target variables
   (from the release snapshot), builds the Octostache dictionary + system
   variables, flattens the process (`DeploymentPlanFlattener` — expands
   StepGroups and ForEach), and partitions into waves
   (`WavePartitioner.Partition`). Structural layout is identical across
   targets — only substituted values differ — so one canonical context
   drives partitioning. Pre-flight refusals happen *before* the claim, so
   they leave `StartedUtc` null.
3. **Claim** — `ServerTaskLease.TryClaimAsync`: one conditional
   `UPDATE … WHERE Id = @id AND Status = Queued` setting `Running` +
   `LeaseUntil = now + 5 min`. Exactly-once execution comes from this
   claim, not from the channel; it also closes the cancel-vs-claim TOCTOU
   (a task cancelled while queued can never be claimed). `MirrorClaim`
   copies the result onto the tracked entity with properties marked
   NOT-modified, so a later `SaveChanges` cannot re-assert `Running` over
   a concurrent `Cancelled`. `ServerTaskLeaseRenewal` then renews the
   lease every minute for the whole orchestration.
4. **Run waves** — §3.
5. **Finalize** — `DeploymentTerminalStatusResolver.Resolve(mode,
   hasFailed, requiredStepDropped, droppedTargetCount, softFailedCount)`
   maps the outcome to `Succeeded` / `SucceededWithWarnings` / `Failed`,
   written through `ServerTaskStatusWriter` (§7). Retention pruning fires
   from the worker on success.

Dispatch is fire-and-forget per item (`_ = TrackedDispatchAsync(...)`
inside an in-flight gauge used for blue-green drain), but execution is
capped by the B7 `NodeTaskGate` (`Engine:MaxConcurrentTasks`, default 5,
FIFO) acquired for the whole orchestration. `RunbookRunWorker` is ungated
(the hand-off is milliseconds) — see §8 for what that asymmetry implies.

## 3. The wave model

Wave grouping is pure and shared (`KrakenDeploy.Execution/WaveGrouping.cs`),
used identically by the server orchestrator and the offline runner:

- **A wave = one step plus every following step whose
  `StartTrigger == StartWithPrevious`.** A `StartAfterPrevious` step (the
  default) opens the next wave. The first step's own trigger is ignored.
- **Waves run serially** — condition evaluation and cross-wave output
  propagation happen at the wave boundary. **Steps within a wave run in
  parallel. Targets within a target wave run in parallel.**
- `WavePartitioner` classifies each wave `Server` or `Target`. A step is
  server-side when `Config["Octopus.Action.RunOnServer"] == "true"` or its
  type is in `ServerOnlyStepTypes` (currently only
  `Octopus.DeployRelease`). A wave mixing sides is refused
  (`InvalidWaveException` → `Deployment.MixedWaveRefused` audit → fail).
- **Server waves** run in-process on the orchestrator, **once** (not
  per-target), against the canonical target's variables; the role filter
  passes if *any* assigned target matches. Runners:
  `ServerScriptStepRunner` (bash/csharp/fsharp/python) and
  `DeployReleaseStepRunner` (child deployment via the normal create path).
- **Target waves** — `DispatchTargetWaveAcrossTargetsAsync` slices a
  per-target sub-plan (`plan with { Steps = waveSteps }`) and pushes it
  over SignalR (`RunDeploymentAsync`); completion is awaited on a
  `TaskCompletionSource` in `IPendingSubPlanRegistry` keyed by
  (deployment, target, `DispatchId`).

**Division of labor (online deployments):** the *server* evaluates
`StepConditionEvaluator` (Success / Failure / Always / Variable — truthy is
`"true"` case-insensitive or `"1"`) and drives `StepRetryRunner`
(per-attempt timeout, retry markers) per wave. The online agent runs with
`orchestrateSteps:false` and just executes what it is handed. The offline
drop-bundle runner flips `orchestrateSteps:true` and reuses the same shared
components — which is why they live in the dependency-light
`KrakenDeploy.Execution` assembly (single package reference: Octostache; no
Contracts, no Server.*).

**Output variables** (see `output-variables.md` for the full contract): the
agent captures `##octopus[setVariable]` markers per step;
`DeploymentOutputAccumulator` on the server folds each wave's captures into
per-target bags plus a last-writer-wins server bag, and merges them into
every *subsequent* sub-plan's `Variables` (plus the run-condition variable
dictionaries and the `SecretRedactor` for sensitive values). Captures fold
regardless of step success. There is deliberately **no server
re-substitution**: unresolved `#{…}` tokens survive flattening; handlers
Octostache-evaluate config fields at run time; script bodies are never
templated (Octopus parity).

**Wave retry semantics:** a wave-level target retry re-dispatches the
**entire sub-plan**, not the failed step — steps must be idempotent.
Retry/timeout for a wave is the `Max` across the wave's steps.

## 4. Rolling deployments

`RollingWindowResolver` reads `Octopus.Action.MaxParallelism` from the
nearest `Kraken.StepGroup` ancestor (walking `ParentStepId`). The cap
applies only when **every** step in the wave shares the same rolling
ancestor with a parseable positive value; anything else yields no batching
(a typo cannot accidentally serialize a farm to one-at-a-time). Enforcement
chunks the wave's alive targets into contiguous batches: **batches
sequential, targets within a batch parallel**. Audits
(`DeploymentRollingBatchStarted/Completed`) fire only when batching
actually splits.

Two properties to be aware of:

- **It is a blast-radius cap, not a canary gate.** A Required failure in
  batch 1 does not stop batches 2..N — condition evaluation for the wave
  happens up front, and drop-out is applied to `aliveTargets` only at the
  **next wave boundary**. Canary semantics require modeling the validation
  step as a separate earlier wave.
- Server-side waves ignore `MaxParallelism` (they run once).

## 5. Failure modes

`DeploymentFailureMode` lives on the `ServerTask` base (default
`BestEffort`), resolved by `DeploymentTerminalStatusResolver`:

- **BestEffort** — a Required-step failure on target X drops X
  (`DroppedTargetInfo(RequiredStepFailed)`, `Deployment.TargetDropped`
  audit); survivors continue. An agent offline at dispatch is likewise a
  drop (`AgentOffline`). A non-required failure is a local soft-fail: only
  that target's later `Condition=Success` steps skip. When no targets
  survive, the deployment fails.
- **Atomic** — *any* failure (Required drop or soft) flips the
  deployment-global `hasFailed`, so every surviving target skips `Success`
  steps and runs `Failure`/`Always` steps in later waves — the farm-wide
  cleanup that keeps all targets on one version. Terminal resolution is
  masking-free: Atomic plus a Required drop is always `Failed`; otherwise
  any degradation with survivors resolves `SucceededWithWarnings`.

## 6. Durability: leases, deadlines, reconciler, reconnect

### Server side

- **Wave deadline is always armed**: explicit step `TimeoutSeconds` if set,
  else `Engine:MaxTargetWaveDuration` (default 1 h), fresh per attempt.
  Server-side waves are not ceilinged (known residual, §9).
- **Agent-disconnect monitor**: a target continuously disconnected for
  `Engine:AgentDisconnectWaveGrace` (default 2 min) has its sub-plan slot
  cancelled; the wave resolves that target as a failure into the failure
  mode. The grace deliberately exceeds the hub's 30 s offline marking so a
  reconnecting agent can flush its outbox first; reconnect resets the
  clock.
- **Reconciler** (`ScheduledDeploymentDispatchJob`, boot + minutely):
  (1) enqueue due `ScheduledFor` rows; (2) re-signal stale `Queued` rows
  older than 2 min (recovers wake-ups lost to restarts) — both phases are
  read-only-then-enqueue, crash-safe; (3) flip lease-expired `Running`
  *deployments* to `Failed` + `Deployment.Interrupted` via a conditional
  UPDATE that re-checks the lease (a live lease is never touched — this is
  what keeps a draining blue-green slot's runs safe); (4) reap runbook
  runs: lease expired pre-hand-off → `Failed` + `RunbookRun.Interrupted`;
  agent-owned but silent past `Engine:MaxRunbookRunDuration` (default 1 h)
  → `Failed` + `RunbookRun.TimedOut`.
- **After a server restart mid-deployment** the in-memory wave state
  (sub-plan TCSs) is unrecoverable: the run is failed by the reconciler
  once its lease expires — there is no resume. Runbook runs already handed
  to an agent survive a restart (the hub writes their terminal status on
  callback).
- **Cancellation / ownership** — `CancelAsync` performs one guarded
  transition to `Cancelled` (clears schedule + lease) and then best-effort
  pushes a cooperative cancel to the agent (kills the step's process tree,
  10 s reap). The worker re-checks **one ownership predicate** —
  "is this task still `Running` in the DB?" via a **fresh scalar status
  projection** (`IsTaskStillRunningAsync`; the tracked entity is stale) — at
  the dequeue, **every wave boundary, and between every rolling batch**. Any
  non-`Running` status stops dispatch cleanly: this catches both an operator
  `Cancelled` and a reconciler `Failed` (E2 — the pre-fix check tested only
  `== Cancelled` at wave boundaries, so a reconciler-interrupted or
  rolling-mid orchestration kept dispatching batches). In-flight dispatched
  work completes; no new wave/batch starts.
- **Lease-loss teardown** — while a dispatch is in flight, a
  `ServerTaskLeaseRenewal` tick that finds no `Running` row to renew (the
  reconciler orphan-failed the run, or it went terminal on another
  connection) fires a `LeaseLost` token the worker links into the
  orchestration's cancellation. A run parked on an agent that never reports
  is then torn down promptly — **without finalising** (the reconciler owns
  the verdict of a run whose lease it reclaimed) — instead of running
  leaseless until the wave deadline.

### Agent side

- **Reconnect** (`AgentReconnectPolicy`): attempt 0 immediate (rides
  sub-second blips), then full-jitter exponential backoff, 1 s base → 30 s
  cap, unbounded. 401/403 switches to a fixed 5-minute lane (re-enroll
  signal) and self-heals if the credential is restored. The supervision
  loop (`ServerLinkHostedService`) has no broad catch — an unexpected
  supervisor crash stops the host so service-manager recovery restarts the
  agent (a visible crash-loop beats a silent zombie).
- **Outbox** (`ServerLinkOutbox`): all logs, step reports, completions and
  adhoc results go through one process-lifetime FIFO channel — strict
  global order, head retried until acked, at-least-once. Log lines cap at
  5 000 (newest dropped, counted; the local rolling file keeps
  everything); completions are never dropped. An item failing 5
  *consecutive connected* sends is dropped as poison (disconnected waits
  do not count). The buffer is process-lifetime only — agent death
  mid-deploy is the lease/reconciler's problem, not the outbox's.
- **Disconnect never aborts a running step.** The per-run token fires only
  on an explicit server cancel push or supersede; a disconnected step runs
  to completion and its reports buffer.
- **DispatchId idempotency**: re-delivery of the *same* `DispatchId` is
  dropped; a *different* `DispatchId` for the same task supersedes —
  cancels the old attempt and waits up to 30 s for it to unwind. Every log
  and report carries the `DispatchId`, so the server ignores output from
  retired attempts. Ordering guarantee: a wave's step reports are acked
  before its completion, so the server's cross-wave output fold is sound.

### Timer reference

| Constant | Default | Where |
|---|---|---|
| Task lease | 5 min | `ServerTaskLease.LeaseDuration` |
| Lease renewal | 1 min | `ServerTaskLease.RenewInterval` |
| Wave deadline ceiling | 1 h | `Engine:MaxTargetWaveDuration` |
| DeployRelease child-wait ceiling | 1 h | `Engine:MaxDeployReleaseWaitDuration` |
| Disconnect grace (wave) | 2 min | `Engine:AgentDisconnectWaveGrace` |
| Hub offline marking | 30 s | `AgentHub` grace |
| Stale-queued re-signal | 2 min | `ScheduledDeploymentDispatchJob.StaleQueuedGrace` |
| Runbook silent-run ceiling | 1 h | `Engine:MaxRunbookRunDuration` |
| Reconciler cadence | boot + minutely | Hangfire `Cron.Minutely()` |
| Status-writer retries | 5 | `ServerTaskStatusWriter.MaxAttempts` |
| Reconnect backoff | 1 s base / 30 s cap, unbounded | `AgentReconnectPolicy` |
| Auth-failure reconnect lane | 5 min | `AgentReconnectPolicy.AuthFailureDelay` |
| Outbox log cap / poison threshold | 5 000 lines / 5 sends | `ServerLinkOutbox` |
| Supersede unwind | 30 s | `DeploymentExecutor.SupersedeUnwindTimeout` |
| Cancel process-tree reap | 10 s | agent `ScriptRunner` |

## 7. Concurrency controls

- **Exactly-once execution** — the atomic `Queued→Running` claim. Wake-ups
  are at-least-once (create, minutely job and reconciler may all signal
  the same task); the conditional UPDATE means exactly one claimant wins.
- **`ServerTaskStatusWriter`** is the single write path for status
  transitions: reload fresh DB values (including `xmin`), re-guard against
  the *fresh* status (default guard: not terminal), apply, save; retry up
  to 5× on `DbUpdateConcurrencyException`. Reload-first is required, not
  an optimization — `xmin` bumps on every row update and two untracked
  writers (log-sequence allocation, minutely lease renewal) churn it
  constantly, so a long-lived tracked entity's token is stale within
  seconds.
- **xmin optimistic concurrency** — shadow `uint` property mapped to the
  Postgres system column (`IsRowVersion()`, no DDL). Turns
  cancel-vs-finalize and finalize-vs-reconciler races into resolvable
  conflicts instead of lost updates.
- **Lease ownership is by expiry, never by `ClaimedBy`** (forensic only) —
  deliberate, so two blue-green slots on one machine cannot misjudge each
  other's liveness.
- **There is no project/environment mutual exclusion.** Nothing prevents
  two deployments to the same project + environment — even the same
  target — from running concurrently. The only guards are the per-task
  claim and xmin. `BlockConcurrentDeployments` on the manual-intervention
  step is informational only. If Octopus-style "one task per project/env"
  serialization is ever assumed, it must first be built.

## 8. Where the deployment/runbook unification stops

The unification is **data-spine deep, not execution-deep**. Shared: table,
children, status machine, status writer, claim/lease primitives, hub, log
pipeline, cancellation push, provenance, audit plumbing, plan format,
`FailureMode` column. Not shared: the orchestrator.

`RunbookRunWorker` (~385 lines) is a single-target reimplementation of the
deployment dispatch prologue. It builds **one whole plan**, hands it to
**one agent**, releases the lease at hand-off, and lets `AgentHub`
finalize. Consequences:

| Capability | Deployment | Runbook run |
|---|---|---|
| Multi-target fan-out | yes | no (single target) |
| Waves orchestrated server-side | yes | no (agent partitions locally) |
| Server-side steps (`RunOnServer`, `DeployRelease`) | yes | no |
| Rolling windows | yes | no |
| BestEffort/Atomic resolution | yes | no (column exists, unused) |
| Lease renewal during run | yes | no (released at hand-off) |
| Orphan reconcile by lease | yes | pre-hand-off only + silent-run ceiling |
| Offline drop bundle | yes | no |
| Scheduled runs (`ScheduledFor`) | yes | no |
| Cross-wave output accumulator | yes | n/a (single dispatch spans plan) |
| Output variables / step outcomes UI | yes | written but not surfaced |
| Variable source | frozen release snapshot | live resolve |
| Retention | lifecycle-driven, pruned by worker | fixed keep (50), pruned by hub |

**Known gap that follows directly:** the online agent runs with
`orchestrateSteps:false` — no `StepConditionEvaluator`, no
`StepRetryRunner`, legacy break semantics (any step failure stops;
`Failure`/`Always` steps do not run). For deployments the server supplies
that orchestration per wave; for online runbook runs **nobody does**. The
M14 step knobs (`Condition`, `ConditionVariableExpression`, `MaxRetries`,
`RetryDelaySeconds`, `TimeoutSeconds`, `Required` gating) are therefore
honored for deployments and *offline* runs, but effectively dead for
*online* runbook runs, even though the unified `process_steps` schema
carries them for both kinds.

**Path to full unification** (the deltas are small and data-driven):
route runbook runs through `DeploymentWorker`, branching only on variable
source (live vs `Release.VariableSnapshot`), process-snapshot source
(`RunbookRun.ProcessSnapshot` vs `Release.ProcessSnapshot`), lifecycle
gate, `ScheduledFor`, retention keep source, and audit event names. That
deletes `RunbookRunWorker`, `RunbookRunChannel` and the pre-hand-off arm of
`ReconcileOverdueRunbookRunsAsync`, and runbooks inherit waves,
multi-target, server steps, rolling, failure modes, lease renewal, orphan
reconciliation and the output-variable/step-outcome UI — and the step
knobs start being honored online. Secondary dedupe targets:
`DeploymentService.CancelAsync` / `RunbookService.CancelRunAsync` (~40
near-identical lines), the two `RetentionService.PruneAfter*` bodies
(~65 lines each, differing only in keep source),
`OctopusSystemVariablesBuilder.BuildForRunbookRun` (hand-duplicates ~25
lines instead of calling the shared section helpers), the runbook worker's
inline `name[i]` array-key construction (should use the shared
`VariableDictionaryExtensions` — drift risk) and missing
`PackageReferenceResolver` overlay, and the two ~800-line detail Razor
pages sharing an unextracted status-header/log-viewer surface.

## 9. Known residuals & sharp edges

- **Manual intervention auto-approves.** `Octopus.Manual` logs its
  instructions and continues (unattended mode); there is no pause/approval
  gate in the orchestrator.
- Marker-parsing asymmetry: the agent parses `##octopus[...]` on stdout
  **and** stderr; `ServerScriptStepRunner` parses stdout only.
- `ServerScriptStepRunner` does not kill its spawned process on timeout
  (orphan leak; the agent-side `ScriptRunner` does kill the tree).
- Server-side waves have no generic deadline ceiling (target waves do). The
  one exception is the `Octopus.DeployRelease` step, whose child-deployment
  wait is now bounded by `Engine:MaxDeployReleaseWaitDuration` (E3) so a
  never-terminating child cannot pin its parent's `NodeTaskGate` slot forever;
  a ceiling hit is classified `TimedOut`. `ServerScriptStepRunner` steps
  remain unceilinged.
- A `DeployRelease` step is **single-attempt**: its configured `MaxRetries` is
  ignored (E3). A step-level retry would re-invoke the runner, triggering a
  *new* child deployment while the prior (timed-out) child is still running
  (children bypass the gate) — racing up to `MaxRetries+1` concurrent deploys
  of the same release to the same targets and stretching the parent's slot
  hold to `(MaxRetries+1)×` the ceiling. The child deployment carries its own
  retry/failure semantics; the parent step does not re-drive it.
- Rolling `MaxParallelism` never short-circuits (§4) — not a canary.
- Wave retries re-run whole sub-plans — step idempotency is on the author.
- `ServerTask.FormValues` is inert (reserved for prompted variables).
- Naming collision: a second, unrelated `ServerTaskKind` enum
  (Deployment/RunbookRun/SystemJob) exists in
  `KrakenDeploy.Server/Services/ServerTasksService.cs` as a UI projection.
- Stale comment in `ServiceCollectionExtensions` claims restart-dropped
  `Queued` tasks are unhandled; `SignalStaleQueuedAsync` + boot reconcile
  now cover it.
- `AgentHub.PruneRetentionAsync`'s deployment branch is dead code —
  orchestrated deployments never reach the hub's fallback finalize;
  deployment retention fires from `DeploymentWorker`, runbook retention
  from the hub. **E1 makes this a hard invariant, not just an
  accident of routing:** `AgentHub.CompleteDeploymentAsync`'s fallback
  finalize is restricted to `ServerTaskKind.RunbookRun` and refuses while
  the lease is live. A deployment completion arriving with no open sub-plan
  slot (e.g. a buffered wave completion flushed into a *fresh* process after
  a restart) is logged and dropped — never finalized — so it cannot mark a
  whole deployment terminal while its remaining waves are unrun. A
  genuinely-orphaned deployment is failed by the reconciler once its lease
  expires; a live orchestrator finalizes through the sub-plan registry.

## References

- Orchestrator: `src/KrakenDeploy.Server.Transport/DeploymentWorker.cs`,
  `WavePartitioner.cs`, `RollingWindowResolver.cs`,
  `DeploymentTerminalStatusResolver.cs`, `DeploymentOutputAccumulator.cs`,
  `RunbookRunWorker.cs`
- Durability: `src/KrakenDeploy.Server.Data/ServerTaskLease.cs`,
  `ServerTaskLeaseRenewal.cs`, `ServerTaskStatusWriter.cs`,
  `Jobs/ScheduledDeploymentDispatchJob.cs`
- Shared execution: `src/KrakenDeploy.Execution/` (`WaveGrouping.cs`,
  `StepConditionEvaluator.cs`, `StepRetryRunner.cs`, `StepCondition.cs`,
  `VariableDictionaryExtensions.cs`, `OctopusMessageParser.cs`)
- Agent: `src/KrakenDeploy.Agent/Deployment/DeploymentExecutor.cs`,
  `src/KrakenDeploy.Agent.Transport/ServerLinkOutbox.cs`,
  `AgentReconnectPolicy.cs`
- Domain: `src/KrakenDeploy.Server.Core/Domain/Deployments/ServerTask.cs`,
  `Domain/Processes/ProcessStep.cs`
- Related docs: `docs/output-variables.md` (output-variable contract)

## History

| Version | Date | Change |
|---|---|---|
| 1.2 | 2026-07-18 | E-series orchestrator fixes: E1 hub fallback finalize restricted to runbook runs + live-lease refusal (§1/§8/§9); E2 single `IsTaskStillRunningAsync` ownership predicate at wave + rolling-batch boundaries and lease-loss teardown (§6); E3 child deployments bypass the `NodeTaskGate` + `Engine:MaxDeployReleaseWaitDuration` child-wait ceiling + self-recursion refusal (§4/§9, timer table). |
| 1.1 | 2026-07-16 | Corrected concurrency claim: B7 `NodeTaskGate` caps concurrent deployment orchestrations (`Engine:MaxConcurrentTasks`, default 5); `RunbookRunWorker` is ungated. |
| 1.0 | 2026-07-16 | Initial version — full engine map from 3-agent code audit at current main. |

# Execution Engine — Server Tasks, Waves, Targets & Failure Modes

| | |
|---|---|
| **Version** | 1.7 |
| **Date** | 2026-07-22 |
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
FIFO) acquired for the whole orchestration. Since the D1 engine merge (§8)
**both kinds** run through this single gated worker — a runbook run acquires a
`NodeTaskGate` slot and holds the blue-green drain gauge for its whole
orchestration, exactly like a deployment.

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
  clock. Liveness is read from `IAgentConnectionRegistry`, which is
  asymmetric-drop safe (E4): `TryRemove` compare-and-removes the
  target→connection mapping (dropping it only when it still points at the
  disconnecting connection), so a late, out-of-order `OnDisconnectedAsync`
  of a *superseded* connection cannot wipe the mapping a reconnected agent
  already re-registered. A heartbeat-driven `Reaffirm` backstop self-heals a
  wiped mapping within one heartbeat. Before E4 the wipe made a healthy agent
  falsely Offline — its waves killed after this grace, its cancel pushes and
  token revocation silently no-op.
- **Reconciler** (`ScheduledDeploymentDispatchJob`, boot + minutely). Since the
  D1 merge the arms are **kind-agnostic** (both kinds share the orchestrator):
  (1) enqueue due `ScheduledFor` rows (either kind) onto the one task channel;
  (2) re-signal stale `Queued` rows older than 2 min (either kind) — both phases
  are read-only-then-enqueue, crash-safe; (3) flip orphaned `Running` tasks
  (either kind) to `Failed` + a kind-branched `*.Interrupted` audit via a
  conditional UPDATE that re-checks the predicate (a live lease is never touched
  — this keeps a draining blue-green slot's runs safe). Orphaned = lease
  **expired or never stamped**: every live orchestration holds a lease, so a
  null-lease `Running` row is ownerless whatever its kind. (D1 Phase 3 deleted
  the transition-era arm 4 — the `Engine:MaxRunbookRunDuration` drain ceiling
  for legacy pre-D1 hand-off runs — and arm 3's kind-branched null-lease
  exemption that protected those runs at boot. The earlier E9 disconnect-reap
  and pre-hand-off arms were removed by D1 Phase 1.)
- **After a server restart mid-orchestration** the in-memory wave state
  (sub-plan TCSs) is unrecoverable: the run is failed by the reconciler once
  its lease expires — there is no resume. Applies to **both kinds** since D1
  (a runbook run holds a live lease for the whole orchestration).
- **Cancellation / ownership** — `CancelAsync` performs one guarded
  transition to `Cancelled` (clears schedule + lease) and then best-effort
  pushes a cooperative cancel to the agent (kills the step's process tree,
  10 s reap). If the agent is *offline* at cancel time the push is skipped —
  but a disconnected step runs to completion, so on reconnect the agent would
  otherwise keep executing the cancelled task. `AgentHub.RegisterAsync`
  therefore reconciles on every (re)connect (E7): a pure server-side lookup
  re-pushes a cooperative cancel for every task assigned to the reconnecting
  target whose DB status is terminal (`Cancelled`/`Failed`) within the last
  hour — the ones the agent may still be running because it was offline when
  the verdict was recorded. Re-pushing to a task the agent is not running is a
  harmless agent-side no-op. No wire change: the agent does not report its
  in-flight ids. The worker re-checks **one ownership predicate** —
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
  everything). Only a **log line** failing 5 *consecutive connected* sends
  is dropped as poison (disconnected waits do not count); **verdict-class
  items — step/deployment completions and adhoc results — are never
  dropped** (E6): they retry forever with capped backoff, since a lost
  completion would turn a succeeded run into a reaper-`Failed` one. FIFO
  ordering makes this a head-of-line hold, which is the intended cost of
  the never-drop guarantee. The buffer is process-lifetime only — agent
  death mid-deploy is the lease/reconciler's problem, not the outbox's.
- **Disconnect never aborts a running step.** The per-run token fires only
  on an explicit server cancel push or supersede; a disconnected step runs
  to completion and its reports buffer.
- **DispatchId idempotency**: re-delivery of the *same* `DispatchId` is
  dropped; a *different* `DispatchId` for the same task supersedes —
  cancels the old attempt and waits up to 30 s
  (`SupersedeUnwindTimeout`) for it to unwind. A non-cooperative attempt
  that never unwinds is force-detached, and the new attempt's machine-gate
  acquisition is then **bounded** (`WedgedGateAcquireTimeout`) — on expiry
  it escalates (logs + reports a failed completion) rather than wedging the
  agent forever behind the stuck step. Every log and report carries the
  `DispatchId`, so the server ignores output from retired attempts: the hub
  guards on `IPendingSubPlanRegistry.IsRetiredDispatch` in **both**
  `AppendLogAsync` and (E-C) `ReportStepCompletedAsync` *before* the DB
  persistence half — otherwise a retired attempt's late step report, flushed
  from the outbox after the wave was superseded, would overwrite the current
  attempt's output variables (the upsert key has no dispatch dimension) and
  prematurely compact its staged step lines. `RecordStepResult` keeps its own
  in-memory guard (a retired dispatch no longer matches the open slot).
  Ordering guarantee: a wave's step reports are acked before its
  completion, so the server's cross-wave output fold is sound.
- **Staging isolation** (E8): a step stages under
  `staging/{deploymentId:N}/{dispatchId:N}/{stepIndex}` — the DispatchId
  segment stops a superseding re-dispatch from sharing a dir with the old
  attempt still unwinding (which could upload the *old* attempt's artifacts as
  the new one's). Per-step cleanup runs in a `finally` so it fires on every
  exit path (early download/extract failures and per-step timeout/cancel
  included, not just the normal tail); this *attempt's* dispatch subtree
  (`staging/{deploymentId:N}/{dispatchId:N}`) is swept when its run ends — NOT
  the shared task tree, which would race and delete a superseding sibling
  attempt's live staging; and the entire staging root is wiped at agent boot
  (any tree there is an orphan from a crashed prior process, reclaiming any
  attempt dir a force-detach left behind). All sweeps are best-effort
  (catch-and-log).

### Timer reference

| Constant | Default | Where |
|---|---|---|
| Task lease | 5 min | `ServerTaskLease.LeaseDuration` |
| Lease renewal | 1 min | `ServerTaskLease.RenewInterval` |
| Wave deadline ceiling | 1 h | `Engine:MaxTargetWaveDuration` |
| DeployRelease child-wait ceiling | 1 h | `Engine:MaxDeployReleaseWaitDuration` |
| Disconnect grace (mid-wave, both kinds) | 2 min | `Engine:AgentDisconnectWaveGrace` |
| Hub offline marking | 30 s | `AgentHub` grace |
| Stale-queued re-signal | 2 min | `ScheduledDeploymentDispatchJob.StaleQueuedGrace` |
| Reconciler cadence | boot + minutely | Hangfire `Cron.Minutely()` |
| Status-writer retries | 5 | `ServerTaskStatusWriter.MaxAttempts` |
| Reconnect backoff | 1 s base / 30 s cap, unbounded | `AgentReconnectPolicy` |
| Auth-failure reconnect lane | 5 min | `AgentReconnectPolicy.AuthFailureDelay` |
| Outbox log cap / log-line poison threshold | 5 000 lines / 5 sends | `ServerLinkOutbox` (verdicts never dropped) |
| Supersede unwind | 30 s | `DeploymentExecutor.SupersedeUnwindTimeout` |
| Wedged-gate acquire (post force-detach) | 30 s | `DeploymentExecutor.WedgedGateAcquireTimeout` |
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
- **Deployment serialization by (project, environment, tenant) — F1.** A
  deployment is claimed only when no OTHER `Deployment` of the same
  `(ProjectId, EnvironmentId, TenantId)` is `Running` (Octopus-parity "one
  deployment per project/environment/tenant"). A NULL tenant is its **own** key:
  untenanted deployments serialize among themselves, while different tenants of
  the same project+environment proceed in parallel. Enforced at **claim** time in
  `ServerTaskLease.TryClaimAsync`: for a deployment the claim runs inside
  `pg_advisory_xact_lock(hash64(project, env, tenant))` — a blocking,
  transaction-scoped lock — and re-checks the "no running peer" predicate
  (`RunningDeploymentPeerPredicate`) as a **separate statement**, so two
  concurrent claimants of one key cannot both see "no peer" and both win: the
  lock-loser blocks until the winner commits, then its fresh READ COMMITTED
  snapshot sees the winner's `Running` row and is refused
  (`ServerTaskClaimResult.SerializationBlocked`). The claim wraps the transaction
  in the DbContext's execution strategy because the web host runs with
  `EnableRetryOnFailure`; the retry ambiguity is safe — it can only yield a false
  `NotQueued` (the worker bails on a row it truly claimed → the reconciler fails
  that ownerless `Running` row), never a double-claim. A refused claim leaves the
  task `Queued`. `DeploymentWorker` also evaluates the same predicate **before**
  acquiring a `NodeTaskGate` slot, so a blocked deployment consumes no slot
  (capacity stays available for other keys); the minutely stale-`Queued`
  re-signal retries it once the running deployment goes terminal — **no new
  poller**. Deployments only — a `RunbookRun` is **exempt** (operational tooling
  runs concurrently), expressed as the kind branch in `TryClaimAsync`. The rule
  is **unavoidable** by design: no bypass setting, no per-project opt-out
  (decision 2026-07-18). Backed by the partial index
  `ix_server_tasks_running_deployment_peer` (running deployments only).
  `BlockConcurrentDeployments` on the manual-intervention step remains
  informational only. The only other concurrency guards are the per-task claim
  and xmin.

## 8. The deployment/runbook unification (D1 — execution-deep)

Since the **D1 engine merge** the unification is **execution-deep**, not just
data-spine deep. `DeploymentWorker` is the *single* orchestrator for both kinds:
`DispatchCoreAsync` probes `ServerTask.Kind`, loads the kind-correct subtype, and
wraps it in an `ITaskDispatchSource` accessor the rest of the engine consumes.
The degraded `RunbookRunWorker` + `RunbookRunChannel` are **deleted**; a runbook
run enqueues onto the same `Channel<TenantWorkItem>` and gains everything B1–B7
added (durable dispatch, disconnect reconciliation, wave deadline, cancel,
idempotency, concurrency cap, status guards).

**The accessor** (`ITaskDispatchSource`, locked decision N4 — no jsonb column
moves) branches only the load-bearing forks; two impls
(`DeploymentDispatchSource`, `RunbookRunDispatchSource`):

- **Process snapshot** — `Release.ProcessSnapshot` vs `RunbookRun.ProcessSnapshot`.
- **Variable source** — frozen `Release.VariableSnapshot` (channel-scoped) vs a
  **live** resolve by `ProjectId` (not channel-scoped). Both flow through the
  shared `BuildTargetDispatchContextAsync`, so a runbook run now also gets the
  `PackageReferenceResolver` overlay and the shared `name[i]` array-key formatter
  (the pre-merge drift targets).
- **Freeze gate** — enforced for deployments, **skipped** for runbook runs
  (Octopus parity — runbooks run during freeze windows).
- **Variable-snapshot refusal** — deployment-only (a runbook run has no snapshot).
- **Offline drop / AI diagnosis** — deployment-only.
- **Audit vocabulary** (`TaskAuditVocabulary`) — a runbook run emits **additive
  `RunbookRun.*`** orchestration events, never `Deployment.*` (the
  `SubscriptionMatcher` matches on the event-type-string prefix, so reusing a
  `Deployment.*` name would leak into `Deployment.*` subscriptions).
- **Retention keep source** — the worker's post-success prune kind-branches:
  lifecycle-phase keep (deployment) vs fixed keep per (runbook, environment)
  (runbook). Both fire from the worker now (not the hub).

| Capability | Deployment | Runbook run (post-D1) |
|---|---|---|
| Multi-target fan-out | yes | **yes** (engine + trigger surface — Phase 2) |
| Waves orchestrated server-side | yes | **yes** |
| Server-side steps (`RunOnServer`, `DeployRelease`) | yes | **yes** (RunOnServer now runs on the SERVER, not the target — security fix) |
| Rolling windows | yes | **yes** |
| BestEffort/Atomic resolution | yes | **yes** (engine + trigger surface — Phase 2) |
| M14 step knobs (Condition/retries/timeout/Required) online | yes | **yes** (were dead for online runs pre-D1) |
| Lease renewal during run | yes | **yes** |
| Orphan reconcile by lease | yes | **yes** (arm 3, both kinds, null-lease included — Phase 3) |
| Offline drop bundle | yes | no (deployment-only, by design) |
| Scheduled runs (`ScheduledFor`) | yes | **yes** (engine + trigger surface — Phase 2) |
| Cross-wave output accumulator | yes | **yes** |
| Variable source | frozen release snapshot | live resolve (accessor) |
| Retention | lifecycle keep, worker-fired | fixed keep, **worker-fired** (was hub-fired) |
| Detail read surface (log / step outcomes / output variables / artifacts) | yes | **yes** (shared task components + `/api/runbook-runs/{id}/*` — Phase 2) |

**Transition — CLOSED (Phase 3, 2026-07-22):** the two seams that finalised
*legacy* pre-D1 hand-off runbook runs are deleted — (a) the runbook fallback
finalize in `AgentHub.CompleteDeploymentAsync` (the post-registry path is now a
pure warn-and-drop for either kind), and (b) reconciler arm 4
(`Engine:MaxRunbookRunDuration`, option removed). With them went arm 3's
kind-branched null-lease exemption (a null-lease `Running` run of either kind
is now failed as an orphan), the dead `ServerTaskLease.ReleaseAsync` hand-off
primitive, and the hub's `PruneRetentionAsync` (retention is worker-fired for
both kinds). Pre-production: no deployed instance existed, so there was
nothing to soak.

**Phase 2 — DONE (2026-07-22):** `RunbookService.TriggerAsync` (+
`IRunbookTrigger`, REST `POST /api/runbooks/{id}/runs`, the RunbookDetail
trigger panel) takes `additionalTargetIds` + `scheduledFor` with
`DeploymentService.CreateAsync` semantics (primary-first microsecond-ordered
assignments; future-only schedule, one dispatch path). The run read surface is
live: `RunbookRunDetail.razor` (`/s/{space}/runbook-runs/{id}`) built from
shared task components (`TaskStatusBanner`, `TaskLogView`,
`TaskStepOutcomesGrid`, `TaskOutputVariablesView`, `TaskArtifactsGrid`) that
`DeploymentDetail.razor` now also renders through, plus REST reads
`/api/runbook-runs/{id}/logs|step-outcomes|output-variables|artifacts(/download)`.
Dedupes landed: `ServerTaskCanceller` (one guarded cancel core behind
`CancelAsync`/`CancelRunAsync`) and `OctopusSystemVariablesBuilder`
(`AddTaskScoped` + nullable-release `AddReleaseScoped` replace the hand-rolled
runbook block, behavior-identical).

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
- `AgentHub.CompleteDeploymentAsync` past the sub-plan registry is a pure
  warn-and-drop (Phase 3): the hub never finalizes a task of either kind, so a
  completion with no open slot (e.g. a buffered wave completion flushed into a
  *fresh* process after a restart) cannot mark a whole task terminal while its
  remaining waves are unrun. A genuinely-orphaned task is failed by the
  reconciler (expired or absent lease); a live orchestrator finalizes through
  the sub-plan registry. Retention fires from `DeploymentWorker` for both
  kinds (the hub's `PruneRetentionAsync` is deleted).

## References

- Orchestrator: `src/KrakenDeploy.Server.Transport/DeploymentWorker.cs`,
  `ITaskDispatchSource.cs` (kind accessor), `TaskAuditVocabulary.cs`,
  `WavePartitioner.cs`, `RollingWindowResolver.cs`,
  `DeploymentTerminalStatusResolver.cs`, `DeploymentOutputAccumulator.cs`
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
| 1.7 | 2026-07-22 | **D1 engine merge (Phases 2+3)** — Phase 2 trigger surface: `RunbookService.TriggerAsync` / `IRunbookTrigger` / REST / RunbookDetail UI gain multi-target (`additionalTargetIds`, primary-first microsecond-ordered assignments) + `ScheduledFor` (future-only, one dispatch path) + a `failureMode` knob (BestEffort/Atomic; UI shows it for rolling runs) with `DeploymentService.CreateAsync` semantics. Run read surface: new `RunbookRunDetail.razor` page + `/api/runbook-runs/{id}/logs\|step-outcomes\|output-variables\|artifacts(/download)`; shared task detail components (`TaskStatusBanner`, `TaskLogView`, `TaskStepOutcomesGrid`, `TaskOutputVariablesView`, `TaskArtifactsGrid`) extracted from `DeploymentDetail` and rendered by both pages. Dedupes: `ServerTaskCanceller` (one guarded cancel core), `OctopusSystemVariablesBuilder.AddTaskScoped` + nullable-release `AddReleaseScoped`. Phase 3 legacy deletion (§6/§8/§9): AgentHub runbook fallback finalize → post-registry warn-and-drop for either kind (+ hub `PruneRetentionAsync` deleted); reconciler arm 4 + `Engine:MaxRunbookRunDuration` removed; arm 3 orphan predicate now "expired OR null lease" for BOTH kinds; dead `ServerTaskLease.ReleaseAsync` removed; stale pre-D1 hand-off claims corrected across XML docs. `RunbookRun.TimedOut` audit constant retained as historical. No soak needed pre-production (no deployed instance). CONTRACT CHANGE: none on the agent wire; REST `TriggerRunbookRunRequest` gains optional `ScheduledFor` + `AdditionalTargetIds` + `FailureMode`. |
| 1.6 | 2026-07-19 | **D1 engine merge (Phase 1)** — runbook runs now execute through the single `DeploymentWorker` orchestrator via a kind-branched `ITaskDispatchSource` accessor (§8); they gain waves, multi-target fan-out, server-side steps (RunOnServer now runs on the SERVER — security fix), rolling, failure modes, the M14 step knobs online, lease renewal and orphan reconciliation. `RunbookRunWorker` + `RunbookRunChannel` deleted; `RunbookService.TriggerAsync` enqueues onto the shared task channel. Reconciler arms generalised to both kinds (§6): arm 3 lease-orphan reconcile is kind-agnostic with a kind-branched null-lease predicate; the E9 disconnect-reap + pre-hand-off arms are removed (B3 covers runbook runs); arm 4 (MaxRunbookRunDuration) + the hub runbook fallback finalize are kept INTERIM for one release to drain legacy hand-off runs. Additive `RunbookRun.*` audit vocabulary (`TaskAuditVocabulary`); runbook retention now worker-fired + counts SucceededWithWarnings. CONTRACT CHANGE: runbook dispatch shape (runbook runs now dispatched as per-target sub-plans via the sub-plan registry, not one whole-plan hand-off). Branch `refactor/eng-server-tasks-engine-merge`. |
| 1.5 | 2026-07-19 | E-D engine hygiene: E8 — agent step staging keyed by `{deploymentId}/{dispatchId}/{stepIndex}`, per-step cleanup moved into a `finally` (fires on the early-failure and timeout/cancel paths too), plus a per-task staging sweep on run end and a whole-root sweep at agent boot (§6 Agent side). Log-sequence counter moved off `server_tasks` into a one-row-per-task `task_log_counters` table (atomic `INSERT … ON CONFLICT` allocator) so log appends no longer churn the row's `xmin` (the B5 token) and force `ServerTaskStatusWriter` retries. E9 (**INTERIM**, deleted by D1) — the dispatch reconciler reaps an agent-owned runbook run whose single target has been continuously disconnected past `Engine:AgentDisconnectWaveGrace` (via `IAgentLivenessProbe` + the target's `LastSeenUtc`), and `RunbookRunWorker` re-verifies the target connection at hand-off and fast-fails instead of dispatching into a dead connection id (§6/§8). CONTRACT CHANGE: none (new `task_log_counters` table — migration `AddTaskLogCounters`). |
| 1.4 | 2026-07-19 | E-C hub/transport hygiene (§6): E4 — `InMemoryAgentConnectionRegistry.TryRemove` compare-and-removes the target mapping (+ heartbeat `Reaffirm` backstop) so a late, out-of-order disconnect of a superseded connection cannot make a reconnected agent falsely Offline. E7 — `AgentHub.RegisterAsync` reconciles in-flight cancellations on (re)connect (server-side lookup of the target's terminal-but-recent tasks → re-push cooperative cancel), so an agent offline at cancel time no longer runs the cancelled task to completion. E-C — the `IsRetiredDispatch` guard is mirrored into `ReportStepCompletedAsync` before its DB persistence half (was only on `AppendLogAsync`), so a replayed retired-attempt step report no longer overwrites the current attempt's output variables or prematurely compacts its staged log lines. CONTRACT CHANGE: none. |
| 1.3 | 2026-07-19 | E-B agent-runtime fixes (§6, timer table): outbox drops only log lines as poison — verdict-class items (completions, adhoc results) are never dropped and retry with capped backoff (E6); a superseded non-cooperative attempt that never unwinds is force-detached and the new attempt's machine-gate acquisition is bounded (`WedgedGateAcquireTimeout`) with escalation. Also fixed off-doc: `DeploymentExecutor` singleton lifetime (dead self-update guard, E5), supervisor park on reconnect-refusal, and the output-variable upsert race (`ON CONFLICT`). |
| 1.2 | 2026-07-18 | E-series orchestrator fixes: E1 hub fallback finalize restricted to runbook runs + live-lease refusal (§1/§8/§9); E2 single `IsTaskStillRunningAsync` ownership predicate at wave + rolling-batch boundaries and lease-loss teardown (§6); E3 child deployments bypass the `NodeTaskGate` + `Engine:MaxDeployReleaseWaitDuration` child-wait ceiling + self-recursion refusal (§4/§9, timer table). |
| 1.1 | 2026-07-16 | Corrected concurrency claim: B7 `NodeTaskGate` caps concurrent deployment orchestrations (`Engine:MaxConcurrentTasks`, default 5); `RunbookRunWorker` is ungated. |
| 1.0 | 2026-07-16 | Initial version — full engine map from 3-agent code audit at current main. |

# Manual Intervention — Pause / Approve / Reject

| | |
|---|---|
| **Version** | 1.4 |
| **Date** | 2026-07-30 |
| **Authors** | Domagoj Jugovic, Claude (Opus 5) |
| **Status** | Draft |
| **Technologies** | .NET 10, EF Core 10, PostgreSQL, SignalR, Blazor Server (Radzen), Hangfire |
| **Projects** | KrakenDeploy.Server.Core, KrakenDeploy.Server.Data, KrakenDeploy.Server.Transport, KrakenDeploy.Server, KrakenDeploy.Steps.Manual |

## Purpose

`Octopus.Manual` currently logs its instructions and returns success
("Step auto-approved (unattended deployment mode)"). WP3 turns it into a real
change-control gate: the task **pauses**, an authorized human **approves** or
**rejects** it, and rejection or expiry fails the task cleanly. Target market is
RH state-sector change control, where an unattended approval step is not a
feature gap but a compliance defect.

This document records the design; §9 lists what deliberately does not change.

## 1. Locked decisions (2026-07-06 — not re-litigated here)

- **Approvers = step-defined responsible team(s)**, Octopus-style. An **empty list
  means anyone in the Space** holding `InterruptionViewSubmitResponsible`.
  **Self-approval is allowed** (the user who queued the deployment may approve it).
  The step editor renders a team **multiselect** (`StepUiWidgets.ResponsibleTeams`) over
  the Space's visible teams, with "Everyone" teams excluded at source. Configuration is
  validated at process SAVE as well as at pause time, both through the shared
  `ResponsibleTeamResolver`, so the two layers cannot drift; the pause-time refusal
  remains because a process can also arrive by REST or by import.
- **Per-step optional auto-fail timeout**, global default **72 h**. Expiry fails
  the task exactly like a rejection (cleanup steps honored) with an audit entry
  noting the timeout.
- **Intervention is task-global**: the pause happens *before the step's wave
  dispatches*, not per-target.
- ~~**Offline drop bundles keep log + auto-approve**, with an explicit warning
  line in the bundle log.~~ **CORRECTED (2026-07-29):** this locked decision rested
  on a false premise. `OfflineDropBundleBuilder` had ALWAYS refused a bundle whose
  process contains `Octopus.Manual`; it never auto-approved. Weakening it to
  log-and-proceed would have re-introduced the exact compliance defect WP3 exists to
  remove, so the refusal is KEPT — see §9.

Permissions already exist in `Permission` and are already seeded by
`BuiltInRoles`: `InterruptionView` (1110) and
`InterruptionViewSubmitResponsible` (1111). WP3 wires **enforcement only** — no
new enum members.

## 2. Decisions made by this document

| # | Decision | Rationale |
|---|---|---|
| D1 | `Octopus.Manual` joins `WavePartitioner.ServerOnlyStepTypes`. | A task-global pause cannot be a per-target agent step. This also aligns the online path with the offline one, which has always treated `Octopus.Manual` as server-orchestrated (`OfflineDropBundleBuilder` refuses a bundle containing it). |
| D2 | A `Manual` step sharing a wave with target steps (`StartWithPrevious`) is now a **mixed wave → refused** (existing `InvalidWaveException` path, `*.MixedWaveRefused` audit). No special-case hoisting. | Consistent with the existing mixed-wave rule and loud rather than silently reordering an operator's process. Requires a deliberate `StartWithPrevious` on a Manual step to hit, which is rare and semantically meaningless. |
| D3 | A `Paused` task **holds** its F1 `(project, environment, tenant)` slot — `Paused` joins `DeploymentStatusExtensions.InFlightAfterClaim`. | Precedent + correctness. `PendingOfflineResult` — the existing parked, lease-less state — already holds the key ("a parked offline-drop deployment still holds the key until its result is imported or it is cancelled"). Releasing it would allow release 1.6 to deploy and complete while 1.5 waits for approval, after which an approved 1.5 would **overwrite newer code**. The timeout bounds the hold, and an operator can cancel a paused task from the detail page or its grid row (which also closes the open gate). |
| D4 | Resume is driven by a **DB-persisted execution checkpoint** (§4), not by an in-process parked orchestration. | The 72 h window reliably spans a server restart / patch window. A parked `Task` would leave an approved deployment permanently stuck. The preamble's "the worker persists state" requires this. |
| D5 | `Octopus.Manual` **is allowed in runbook processes**, with identical mechanics. | Octopus allows it, and post-D1 both kinds share the orchestrator, so it costs nothing. A runbook run resumes with **live-resolved variables** (§4.3) — that is the runbook variable contract, not a regression. |
| D6 | The 72 h global default is `Engine:DefaultInterventionTimeout` on `EngineOptions`, validated by `EngineOptionsValidator`. | House rule 11 defers engine knobs to F3's Engine settings document *once F3 has landed*; F3 is still open (master plan §4). This mirrors what F2 did with `MaxTargetQueueWait`: ship the config-file knob now, fold it into the Engine document when F3 lands. Recorded as an **F3 breadcrumb** below. |
| D7 | Responsible teams are stored as an **immutable Guid snapshot** on the `Interruption`, not a join table with a real FK to `teams`. | The interruption is a permanent change-control record. A composite-FK join row would either block team deletion or cascade away the historical record of who was responsible. House rule 4 mandates a stamped `space_id` on join tables; it does not mandate a join table. |

## 3. Pause semantics on the B-series spine

A new **non-terminal** `DeploymentStatus.Paused` is written through
`ServerTaskStatusWriter` (B5 `xmin` reload-guard-write), never by a bare
`SaveChanges`.

At the wave boundary immediately before a wave containing a `Manual` step, the
orchestrator:

1. Persists the execution checkpoint (§4) and creates a `Pending` `Interruption`
   row in one transaction with the `Running → Paused` transition.
2. **Clears the lease** (`ClaimedBy`/`LeaseUntil` = null) and disposes the
   `ServerTaskLeaseRenewal`.
3. **Returns from `DispatchCoreAsync`**, which releases the `NodeTaskGate` slot
   and the blue-green in-flight gauge via their existing `using` scopes. No
   thread and no gate slot are held across the approval window.

**Reaper exemptions.** Both come out for free and are asserted by tests rather
than by new branches:

- **B1 reconciler** — `OrphanedRunningPredicate` is
  `Status == Running && (LeaseUntil < now || LeaseUntil == null)`. `Paused` is
  not `Running`, so the orphan arm cannot see it. Arms 1–2 only signal `Queued`.
- **B3 disconnect monitor / wave deadline** — both are per-dispatch constructs
  scoped to an in-flight sub-plan. A paused task has no in-flight dispatch, so
  there is nothing armed.

**Re-signal durability.** A new reconciler **arm 3** re-signals `Paused` tasks
whose gate is already resolved but which have not yet been picked up — the
crash-safe mirror of arm 2 for the pause path. The DB
(interruption status) is the source of truth; the channel item is an
at-least-once wake-up, exactly as B1 requires.

**Resume claim.** `ServerTaskLease.TryResumeAsync` performs one conditional
`UPDATE … WHERE Id = @id AND Status = Paused` → `Running` + fresh lease. It does
**not** re-run the F1 advisory-lock peer check: per D3 the key was never
released, so no peer can hold it, and re-checking would let a task lose a slot it
already owns. The SAME exemption applies to the worker's pre-gate probe
(`ProbeGateAsync`) — missing it there deadlocked a resume against a due,
earlier-queued sibling. The guard additionally requires the gate to be ANSWERED, so a
duplicate wake-up cannot resume an open gate. `TryClaimAsync` (`Queued → Running`) is
untouched.

## 4. The execution checkpoint

### 4.1 What is captured

Everything the wave loop carries in memory that is not recomputable from the
frozen snapshot:

| Field | Why it cannot be derived |
|---|---|
| `ResumeWaveIndex` | The wave to restart at. |
| `HasFailed` | Drives `Condition=Success/Failure/Always` evaluation for later waves. |
| `AliveTargetIds` | Drop-out is applied to `aliveTargets` in memory only. |
| `DroppedTargets` (target, reason, step, error) | Feeds `DeploymentTerminalStatusResolver`; `TargetDropped` audits are prose, not structured state. |
| `SoftFailedTargetIds` | Per-target `Condition=Success` skipping in BestEffort. |
| Output bags — per-target and server (values + sensitive key names) | `TaskOutputVariable` rows are keyed `(task, stepKey)` with no target dimension, so the per-target bags are **not** recoverable from them. |

`DeploymentOutputAccumulator` gains a restore path that seeds the four bags and
replays the `VariableDictionary` stamping and the `SecretRedactor` folds, so a
resumed run's later waves see prior outputs exactly as an uninterrupted run
would.

### 4.2 Where it lives

A single `jsonb` column on `server_tasks`, **encrypted** via `IEncryptionService`
(AES-GCM at rest) because the output bags contain sensitive captured values —
the same treatment `TaskOutputVariable` already gives them. The member is named
`*Encrypted` so the DEK-rotation completeness test
(`DekRotationWalkTests`) picks it up, and `DekRotationWalk.ReEncryptAllAsync`
gains the corresponding walk step. The column is cleared on resume and on
terminal status.

### 4.3 What is rebuilt rather than restored

`DispatchCoreAsync` re-runs its prep on resume: target set, per-target dispatch
contexts, flatten, and wave partitioning. For a **deployment** this is
deterministic — `Release.ProcessSnapshot` and `Release.VariableSnapshot` are
frozen. For a **runbook run** the process snapshot is frozen on the run but
variables resolve **live**, so a run paused across a variable edit resumes with
the new values. That is the documented runbook variable contract
(`execution-engine.md` §8), not a pause-specific defect.

Invariants are enforced, not papered over (pre-production policy): a checkpoint
whose `AliveTargetIds` are no longer in the task's assignment set, or whose
`ResumeWaveIndex` is out of range for the re-partitioned wave list, **fails the
task** with a clear reason instead of silently continuing.

## 5. Data model

`Interruption` — `ISpaceScoped`, composite-FK per house rule 4
(`ConfigureSpaceScopeAsChild()`, FK `(space_id, task_id)` → `server_tasks
(space_id, id)`, `ON DELETE CASCADE`), mirroring
`TaskStepOutcomeConfiguration`:

- `TaskId`, `StepIndex`, `StepName`
- `Instructions` — Octostache-resolved at pause time so the approver reads real
  values, against a bag whose SENSITIVE VALUES ARE MASKED FIRST (v1.2). Masking
  the dictionary rather than redacting the rendered output is the only correct
  order: `SecretRedactor` is an ordinal substring match, so `#{Secret | ToBase64}`
  — or `| ToUpper`, `| Md5`, all shipped in Octostache 3.9.2 — produced a string
  it could not recognise, and the transformed secret persisted in a cleartext
  column readable with `InterruptionView` alone.
- `ResponsibleTeamIds` (`Guid[]`, snapshot — D7) **and `ResponsibleTeamNames`**
  (`text[]`, v1.2). Names are captured at pause time because they are frequently
  NOT recoverable later: the break-glass path (§6) exists precisely because a
  named team can be deleted while the gate waits, and the audit entry would then
  render as bare GUIDs.
- `Status`: `Pending` / `Approved` / `Rejected` / `TimedOut` / **`Cancelled`**
  (v1.2 — the task went terminal underneath an unanswered gate; resolved but NOT
  a decision, so it resumes nothing and is never audited as an approval or a
  refusal).
- `ExpiresUtc` — **always set** for gates created from v1.2 on (see §7).
- `ActedByUserId`, `ActedByDisplay`, `Notes`, `CreatedUtc`, `ActedUtc`
- Unique index `(task_id, step_index)` — one interruption per step per task.

### Lifetime — corrected in v1.2

v1.1 called this row the durable change-control record. **It is not, and never
was.** The FK is `ON DELETE CASCADE` and `RetentionService` hard-deletes tasks, so
the row dies with its task: after `RetentionKeepDeployments` further green
deployments, "who approved release 2.3.0 to Prod, when, with what notes" was gone.
The `Guid[]` snapshot and denormalised display were justified on that false
premise.

The durable record is the `Interruption.*` **audit entry**, held outside the
ordinary audit window by `PerformanceSettings.ChangeControlAuditRetentionDays`
(default `0` = never purge; surfaced on the Performance page). Consequences:

- The resolution audit entry must be **self-contained**: decision, step name and
  index, resolved team NAMES, responder id and display, both timestamps, notes,
  and any break-glass marker.
- The row's snapshots keep a better justification — they must be stable **while
  the gate is open**, so a mid-window rename or deletion cannot retroactively
  change who was asked, and a 72 h wait cannot leave the panel unable to render.
- `CASCADE` stays. The row is operational state for a live gate and it is right
  that it dies with its task. `RESTRICT` would block retention outright, and
  `SET NULL` on a composite Space FK needs the PG15+ `SET NULL (col)` form and
  leaves an unattributable orphan.

Step config gains two keys alongside the existing Octopus namespace:
`Octopus.Action.Manual.ResponsibleTeamIds` (already defined, now **enforced**)
and a Kraken-native `Kraken.Action.Manual.TimeoutHours`.

`StepOutcomeKind` is extended **additively**, as its doc comment anticipates:
`ManualInterventionApproved = 4`, `ManualInterventionRejected = 5`,
`ManualInterventionTimedOut = 6`.

## 6. Authorization

`InterruptionService` is the authority; the UI gate is cosmetic.

- **Read** — `InterruptionView`, Space-scoped.
- **Respond** — `InterruptionViewSubmitResponsible`, Space-scoped, **plus** team
  matching: if `ResponsibleTeamIds` is non-empty the caller must be a member of
  at least one listed team. Membership comes from
  `IPermissionEvaluator.GetUserTeamIdsAsync` — the same resolver RBAC uses — so it
  merges **all three** sources: explicit `TeamMember` rows, `TeamExternalGroup`
  matches against the user's *persisted* IdP groups (not cookie claims), and the
  applicable "Everyone" teams. That third source is why the orchestrator **refuses**
  a gate naming an Everyone team: it would match every authenticated user while the
  UI reported a restriction as enforced. An empty list degrades to the permission
  check alone. `AdministerSystem` is a **break-glass override**, recorded *as* an
  override in the audit trail — required because the team snapshot has no FK, so
  deleting the named team would otherwise leave the gate unanswerable by everyone
  while it kept holding the F1 slot.
- Blazor handlers re-check server-side with
  `Guard.AllowAsync(Permission.InterruptionViewSubmitResponsible, bypassCache: true)`
  before calling the service (house rule 2).
- **Notes are mandatory on reject**, enforced in the service, not only the
  dialog.

## 7. Timeout

`ExpiresUtc = CreatedUtc + (step TimeoutHours ?? Engine:DefaultInterventionTimeout)`.
A minutely Hangfire job sweeps `Pending` interruptions past `ExpiresUtc`, marks
them `TimedOut`, and resumes the task down the **same rejection path** (§8) with
an audit entry naming the timeout. Registered per-account via the existing
multi-account job fan-out; work items carry `AccountId` (house rule 5).

**Every gate is bounded (v1.2).** v1.1 accepted `TimeoutHours = 0` as an explicit
"wait forever", which produced a NULL `ExpiresUtc`. The sweeper filters on
`ExpiresUtc != null`, and `Paused` is in `InFlightAfterClaim` — so such a gate was
never reaped while its task kept holding the `(project, environment, tenant)` key.
An author holding only `ProcessEdit` could therefore block every later release of
that project and environment until somebody with `TaskCancel` intervened: a
denial-of-release created by editing a process. Both `0` at the step level and a
zero `Engine:DefaultInterventionTimeout` are now refused — at process save and at
startup respectively, since permitting the latter would only move the same problem
into a config file. Raise the timeout (up to one year) instead of disabling it.

## 8. Resume, rejection and cleanup

Approve, reject and timeout all resume the orchestration through the identical
path — only the recorded outcome differs:

- **Approve** — the Manual step records `ManualInterventionApproved`; the wave
  loop continues from `ResumeWaveIndex` with checkpoint state restored.
- **Reject / timeout** — the Manual step records
  `ManualInterventionRejected`/`ManualInterventionTimedOut` and the run is marked
  **rejected**. It does **not** take the "Required server step failed →
  `FailAsync` → return" shortcut, because that path skips cleanup. Instead
  `hasFailed` is set so later waves' `Condition=Failure`/`Always` steps run per
  `FailureMode`, and finalisation resolves **`Failed`** — a dedicated
  `rejected` input to `DeploymentTerminalStatusResolver`, since
  `hasFailed` alone resolves to `SucceededWithWarnings`.

## 9. Deliberately unchanged

- **Offline drop bundles are REFUSED** at generation time when the process contains
  `Octopus.Manual` (`OfflineDropBundleBuilder`) — the long-standing behaviour, kept
  deliberately. An air-gapped target cannot reach an approver, so the only choices are
  to refuse or to pass a change-control gate with no human decision; refusing is the
  stronger one. The step package's handler still carries a loud
  `APPROVAL NOT ENFORCED` warning for any runner that reaches it via a hand-built plan.
- Tasks whose process contains no `Manual` step take no new code path.
- `BlockConcurrentDeployments` remains informational — F1 already enforces
  one deployment per `(project, environment, tenant)` unconditionally.
- No agent wire change: the pause is entirely server-side, and `Octopus.Manual`
  stops reaching an agent at all online (D1).

## 10. Contract changes

- **EF schema** — new `interruptions` table; `server_tasks` gains the encrypted
  checkpoint column; `DeploymentStatus` gains `Paused = 7`; `StepOutcomeKind`
  gains 4–6. All additive.
- **REST** — additive interruption read + respond endpoints.
- **No gRPC `.proto` or SignalR hub-interface change.**

## 11. Known limits / follow-ups

All five are TRACKED, so this section is a description and the master plan is the
state: the `Engine:DefaultInterventionTimeout` breadcrumb is folded into the **F3**
row, and the other four are **WP3-c** (`docs/master-plan-2026-07-18.md`). Nothing here
is an untracked intention.

- **F3 breadcrumb**: fold `Engine:DefaultInterventionTimeout` into F3's Engine
  settings document (D6).
- **Process validation has no surface.** `ProcessService.ValidateAsync` — including
  the v1.2 gated-child advisory — has no caller anywhere: no REST endpoint, no UI.
  The advisory reaches the operator only via the step editor's post-save
  notification (`DescribeGatedChildAsync`). Structural validation ERRORS are
  equally unsurfaced, which is pre-existing and not WP3's doing.
- **A runbook run's live variables can invalidate its checkpoint.** Runbook runs
  resolve variables live and have no snapshot, so a variable driving a `ForEach`
  collection changes the flattened wave count. Editing it during the approval
  window makes `RestoreFromCheckpoint` fail the run with "the approved process no
  longer matches", blaming the process for a variable edit — and that abort runs
  before the resume branch, so no `Failure`/`Always` cleanup executes.
- **`PermissionEvaluator.UserIsSystemAdminAsync` ignores the role assignment's own
  `SpaceId`**, so `AdministerSystem` granted for one Space is global. WP3's
  break-glass check passes a scope, but that only closes the call site. Pre-existing
  and out of WP3's scope; WP3 is the first consumer that uses god mode to override
  a non-permission, per-step authorization dimension, so the blast radius grew.
- Server-side waves still have no generic deadline ceiling
  (`execution-engine.md` §9); a paused wave is bounded by its interruption
  timeout instead, which is the stronger guarantee.
- The checkpoint is written only at a pause boundary. Ordinary
  crash-mid-orchestration behavior is unchanged: no lease, reconciler fails it.

## References

- `docs/execution-engine.md` — §6 durability, §7 concurrency, §8 D1 unification
- `docs/master-plan-2026-07-18.md` — WP3 scope, F3 status
- `docs/production-readiness-audit-2026-07-13.md` — the audited defect
- `src/KrakenDeploy.Server.Transport/DeploymentWorker.cs` — orchestrator
- `src/KrakenDeploy.Server.Data/Jobs/ScheduledDeploymentDispatchJob.cs` — reconciler
- [Octopus manual intervention docs](https://octopus.com/docs/projects/built-in-step-templates/manual-intervention-and-approvals)
  (clean-room contract source)

## History

| Version | Date | Change |
|---|---|---|
| 1.4 | 2026-07-30 | §11 residuals are now TRACKED rather than described: the `Engine:DefaultInterventionTimeout` breadcrumb (plus `Engine:MaxDeployReleaseGatedWaitDuration`, both positive-only) folded into the master plan's **F3** row, and the remaining four grouped as **WP3-c** "polishing". Three of those four are pre-existing defects WP3 illuminated rather than caused, which is why they are a separate WP and not a WP3 follow-up commit. |
| 1.3 | 2026-07-30 | Corrects §11: two entries were listed as open but had already been fixed later in the same WP3-b pass — `ForEach` flatten warnings no longer re-emit on a resume dispatch (`DeploymentWorker.isResumeDispatch`), and an API-key response is now labelled `(via API key)` in the responder display (`InterruptionService.ResponderLabel`). No code change; the section was written before those two fixes landed and not revisited. |
| 1.2 | 2026-07-30 | **WP3-b — remediation of the max-effort review** (§5 rewritten, §7, §11). The §5 LIFETIME premise is retracted: the `interruptions` row is CASCADE-deleted with its task and retention deletes tasks, so it never was the durable change-control record — that is now the `Interruption.*` audit entry behind `ChangeControlAuditRetentionDays` (default never-purge), with `ResponsibleTeamNames` snapshotted and the resolution entry made self-contained. Instructions are masked in the variable DICTIONARY, because Octostache filters defeat redact-after-evaluate. `Cancelled` documented; approval is now an allow-list, so it (and any future status) refuses rather than proceeding. `TimeoutHours = 0` and a zero engine default are both refused — an unexpiring gate held the F1 key forever. The freeze exemption on resume is now conditional on the task having executed nothing. New §11 residuals: unsurfaced process validation, the runbook live-variable checkpoint mismatch, `ForEach` warnings re-emitting per resume, API-key responses indistinguishable from human ones, and the unscoped system-admin check. CONTRACT CHANGE: `interruptions.responsible_team_names` (`text[]`). |
| 1.1 | 2026-07-29 | Post-review corrections. The offline-drop locked decision is retracted (§1, §9) — the builder always REFUSED `Octopus.Manual` and that is kept. §6 now names all three team-membership sources, records the Everyone-team refusal and the audited `AdministerSystem` break-glass override. §1 notes the approver field shipped as free-text GUIDs, not the multiselect. Instructions are REDACTED before storage; gate run conditions are evaluated (a gate can be Skipped); the resume guard requires an answered gate; the F1 pre-gate and the freeze gate are both exempt on resume; the checkpoint carries `InterventionRejected`; cancel closes open gates via the new `InterruptionStatus.Cancelled`. |
| 1.0 | 2026-07-29 | Initial design for WP3. |
</content>
</invoke>

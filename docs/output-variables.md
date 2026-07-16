# Output Variables — Cross-Step Propagation & Capture

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-16 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, Octostache, SignalR, PostgreSQL |
| **Projects** | KrakenDeploy.Server.Transport, KrakenDeploy.Server.Data, KrakenDeploy.Contracts, KrakenDeploy.Execution, KrakenDeploy.Agent |

## Purpose

Covers the B4 batch (T0-4, T1-6). Before B4, output variables silently did
**not** propagate between steps on the online deployment path: with the
default `StartAfterPrevious` trigger every step is its own wave (its own
sub-plan dispatch), the per-target dispatch context — including
`Plan.Variables` — was built once before any step ran, and the agent's
accumulator only spans a single dispatch. So
`#{Octopus.Action[Step1].Output.X}` in step 2 resolved to nothing online,
while offline drops and runbooks (whole plan, one dispatch) worked.
Separately, server-side steps (`ServerScriptStepRunner`) never captured
`Set-OctopusVariable` output at all — and logged the raw
`##octopus[setVariable]` marker line verbatim, base64-encoded **sensitive
values included**.

## The resolution contract (established, now uniform)

Where an output reference resolves is a *run-time* concern, identical
offline and online (and matching Octopus):

- **Config fields** (IIS site name, package path, manual instructions, …):
  step handlers Octostache-evaluate config values against `Plan.Variables`
  at execution time — `#{Octopus.Action[Step1].Output.X}` in a config field
  resolves.
- **Script bodies**: never templated at run time (Octopus parity; scripts
  legitimately contain literal `#{…}` text). Scripts read
  `$OctopusParameters['Octopus.Action[Step1].Output.X']` / env vars — built
  from the same `Plan.Variables`, server-side and agent-side alike.
- The flattener's build-time substitution leaves unresolved tokens verbatim,
  so nothing is destroyed before run time.

The key shape `Octopus.Action[<key>].Output.<name>` — where `<key>` is the
step's `AccumulatorKey` (ForEach synthetic key like `Deploy[0]`) or display
name — lives in `KrakenDeploy.Contracts.OutputVariableAccumulator`, shared
by the agent's within-dispatch accumulation and the server's cross-wave
merge. One source of truth; byte-identical keys.

## 1. Online cross-wave merge (`DeploymentOutputAccumulator`)

The orchestrator folds every wave's captured outputs and augments every
subsequent dispatch:

- **Per-target bags** — a target's later waves see *its own* captured value
  for machine-specific outputs (parity with the agent's accumulator). Folded
  from each wave's drained per-step reports **regardless of step success**
  (also parity — a failed step's rollback marker is consumed by
  `Condition=Failure/Always` cleanup steps).
- **Server view** — server waves get a last-writer-wins fold across targets
  (matching the existing parallel-collision audit semantics) via an
  augmented env view and a cloned condition bag (a clone because the
  canonical `VariableDictionary` *is* target[0]'s condition bag, and
  per-target isolation must hold for it).
- **Run conditions** — folds stamp merged keys into the per-target and
  server-wave dictionaries, so `Variable` run-conditions can reference
  prior outputs.
- **Sensitivity (T0-6)** — the sensitive-name set now rides through
  `SubPlanStepResult` into the fold: a sensitive output's merged key extends
  the next plan's `SensitiveVariableNames` (the agent's redactor masks it in
  later waves' logs) and its *value* folds into the server-side redactor
  immediately. Values still travel plaintext inside the (TLS-protected,
  agent-authenticated) sub-plan — same trust surface as ordinary sensitive
  variables.

B2's outbox ordering makes the fold sound: a wave's step reports are
acknowledged (hub-processed, DB-upserted) *before* its completion resolves
the wave, so the drained bag is complete when the orchestrator folds it.

## 2. Server-side capture (`ServerScriptStepRunner`, T1-6)

Server steps now parse stdout through the shared
`KrakenDeploy.Execution.OctopusMessageParser` — agent parity:

- `##octopus[setVariable]` → captured (name, value, sensitive flag); the
  marker line is **consumed, never logged** — this also fixes the pre-B4
  leak of base64-encoded sensitive values into the task log.
- A sensitive value folds into the redactor immediately: subsequent lines
  echoing it are masked.
- `stdout-warning`/`stdout-error`/`stdout-default` sticky levels,
  `createArtifact`/`progress` informational lines — as on the agent.
- Captures thread through `StepRetryRunner` (final attempt only — a failed
  attempt's partial outputs are discarded) into the wave fold and into
  `TaskOutputVariableStore` — the hub's upsert extracted verbatim, so both
  capture sources share one persistence + encryption path.

## Semantics reference

| Aspect | Behaviour |
|---|---|
| Same-target later wave | Sees the target's own captured value |
| Other target's capture | Not visible (machine-scoped; qualified cross-machine syntax is a future item) |
| Server step reading captures | LWW fold across targets + all server captures |
| Server capture visibility | All targets' later waves + later server steps |
| Failed step's captures | Propagate (cleanup-step consumption) |
| Wave retry | Final attempt's captures win |
| Sensitive captures | Encrypted at rest; masked in logs both sides; merged key added to `SensitiveVariableNames` |
| Offline / runbooks | Unchanged (whole-plan dispatch; agent accumulator) |

## Acceptance & verification

- `OnlineOutputVariableFlowTests` — the online hand-off (wave-2 sub-plan
  carries wave-1 captures), multi-target isolation, sensitive-name
  extension, failed-step propagation to a `Condition=Always` cleanup step.
- `ServerSideOutputCaptureTests` — REAL shell processes: capture + marker
  suppression + live masking; and the full round trip through the real
  orchestrator (fake-agent output resolved by a real PowerShell server step
  via `$OctopusParameters`, whose own capture reaches the last agent wave's
  plan and the DB store).
- `CrossIterationOutputResolutionTests` (scope 3) — unchanged and green:
  ForEach synthetic-key resolution is untouched.

## Known residuals (deliberate, tracked)

- Qualified cross-machine references
  (`Octopus.Action[Step].Output[Machine].X`) are not implemented.
- Script bodies are not templated at run time — by design (Octopus parity;
  user-confirmed 2026-07-16). Use `$OctopusParameters`.
- Markers on **stderr** are not parsed (stdout only, matching Octopus);
  the agent parses both — noted as a minor divergence.
- Server-side steps still spawn orphan processes on timeout (pre-existing,
  B7 territory).

## References

- `docs/production-fix-prompts-2026-07-13.md` §B4 — spec; the "re-run
  Octostache server-side" mechanism it sketched was unnecessary (recon:
  tokens survive the flatten; run-time resolution is the contract).
- `docs/agent-reconnect.md` — B2 outbox ordering the fold leans on.
- `architecture.md` "Step composition" — the M15.2 accumulator-key contract.

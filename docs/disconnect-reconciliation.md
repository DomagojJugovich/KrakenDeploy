# Disconnect Reconciliation & Wave Deadlines

| | |
|---|---|
| **Version** | 1.3 |
| **Date** | 2026-08-18 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8), Claude (Fable 5) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR, EF Core 10, PostgreSQL |
| **Projects** | KrakenDeploy.Server.Transport, KrakenDeploy.Server.Data, KrakenDeploy.Server |

> v1.3 (2026-08-18): note for F6 — the per-plan target-exclusion deferral
> happens at CLAIM time, BEFORE dispatch: a target-blocked task stays `Queued`
> and never arms a wave deadline, so F2's two-stage queue-wait backstop
> arithmetic in §1 is untouched. `Engine:MaxTargetQueueWait` continues to bound
> only the AGENT-side machine-gate queue of a dispatched sub-plan (in practice
> now contended by ad-hoc work and cross-process gates only — a competing plan
> is already refused at claim).
>
> v1.2 (2026-07-25): §1 updated for F2 — the wave deadline now arms in TWO
> stages (dispatch-time backstop, then a clamped re-arm on the agent's
> gate-acquisition report) and `Engine:MaxTargetQueueWait` is documented. All
> `Engine` durations are validated at startup.
>
> v1.1 (2026-07-22): §4 rewritten and `Engine:MaxRunbookRunDuration` removed —
> the D1 engine merge routed runbook runs through the unified orchestrator
> (they hold a lease for the whole orchestration), and D1 Phase 3 deleted the
> transition-era hand-off reap (reconciler arm 4 + the hub fallback finalize).

## Purpose

Covers the B3 batch (T0-3): the server must never wait on an agent forever.
Before B3, a deployment stranded in `Running` when an agent dropped mid-wave
with the default step config: `DispatchTargetWaveAsync` armed `CancelAfter`
only when `TimeoutSeconds > 0` (default 0 = unlimited), so it awaited the
sub-plan TCS with no deadline. The B1 reconciler correctly never intervened —
the worker's lease renewal runs for as long as the dispatch is in flight, and
the process *is* alive. The `using`-scoped `InFlightWorkGauge` tracker never
disposed, and `ReleaseDrainDecision.ShouldRetire` treats any in-flight work as
never-retire — **one dead agent blocked blue-green retirement indefinitely**.
Runbook runs were worse: after B1's hand-off nothing could ever finalize a run
whose agent died, and a dispatch that died *before* the hand-off was invisible
to the reconciler's deployments-only orphan step.

## Resilience posture (decided)

**Every await on an agent has a ceiling, and a vanished agent fails the wave
long before the ceiling.** The disconnect grace deliberately exceeds the hub's
30 s offline-marking grace because the B2 agent reconnects with unbounded
retry and *flushes buffered wave results* on reconnect — cancelling too early
discards work the flush would have delivered. All ceilings live in the
`Engine` configuration section (`EngineOptions`); no `Engine` section means
the shipped defaults stand.

## 1. Server-side wave deadline

`Engine:MaxTargetWaveDuration` (default 1 h) replaces "unlimited" when no step
in the wave configures `TimeoutSeconds`. An explicit step timeout is honoured
as-is, even above the ceiling — operator intent wins; the ceiling only kills
the *await forever* path. The deadline is armed per dispatch attempt (wave
retries each get a fresh window), and the ceiling-based timeout message is
distinct from a configured step timeout so operators can tell them apart.
Server-side waves — including manual-intervention gates that legitimately wait
hours — are **not** subject to this ceiling. Non-positive configuration falls
back to the default rather than reintroducing an unbounded wait.

**F2 made the arming two-stage.** The budget above is EXECUTION time, and a
sub-plan can sit in the agent's machine execution queue before it executes at
all — pre-F2 that queue time burned the wave's budget, so an operator's 30 s
step timeout blew up purely because the box was busy. Now:

1. at DISPATCH the attempt is armed with the **backstop ceiling** = budget +
   `Engine:MaxTargetQueueWait` (default 2 h), which is what keeps this
   document's core invariant — *the wave deadline is always armed* — true for an
   agent that stays connected but never executes;
2. when the agent reports gate acquisition (`ReportExecutionStartedAsync`) the
   timer is **re-armed with the execution budget alone**, clamped so the re-arm
   can never push the attempt past the stage-1 backstop instant.

A stage-1 (backstop) hit reports "never started executing"; a stage-2 hit
reports the ordinary duration message. The distinction matters because the
operator fix differs: a busy or wedged machine, versus a slow step.

All `Engine` durations are validated at startup (`EngineOptionsValidator`,
`ValidateOnStart`). A bare number binds as DAYS — `"MaxTargetQueueWait": "4"`
means four days — and a multi-week value overflows `CancelAfter`, which would
fail EVERY dispatch. Both are refused by key name at boot instead.

## 2. Mid-wave disconnect monitor (worker-side)

While a wave attempt is awaited, the worker samples the agent connection
registry (poll = `clamp(grace/4, 25 ms, 5 s)` — one registry lookup per poll
per in-flight wave). A **continuous** disconnect past
`Engine:AgentDisconnectWaveGrace` (default 2 min) cancels the pending sub-plan
slot; the wave resolves as a failure ("agent disconnected mid-wave") into the
deployment's failure mode — BestEffort drops the target and survivors
continue, Atomic fails the deployment farm-wide. A reconnect within the grace
resets the clock: the B2 flush then resolves the wave normally. Zero/negative
grace disables the monitor (the wave deadline still applies).

Detection is worker-side rather than hub-driven (the spec's original sketch)
— the state lives with the await, every disconnect cause is covered without a
fire-and-forget timer in the hub, and a reconnected agent (new connection id)
is naturally recognized. Cancelled attempts retire their `DispatchId` (B2), so
a later flush of that attempt is swallowed as stale rather than corrupting a
re-dispatched attempt.

## 3. Wave-retry connection refresh (B7 sliver, pulled forward)

Wave retries used to re-dispatch to the connection id captured before attempt
1 — after a disconnect that id is dead, and SignalR's `Clients.Client()` to an
unknown id silently no-ops, burning a full deadline window per retry. Each
retry attempt now re-reads the registry: offline → the remaining retries are
abandoned ("agent went offline during the wave"); reconnected → the fresh id
is used. The refresh applies the same P3-8 cross-account guard as the initial
dispatch (a cross-account hit is treated as offline). B7 still owns the rest
of retry re-resolution (variable re-snapshot, node concurrency).

## 4. Runbook-run reap (superseded by the D1 engine merge)

Post-D1 a runbook run is orchestrated by `DeploymentWorker` exactly like a
deployment: it holds (and renews) the dispatch lease for the **whole**
orchestration, so the ordinary lease-orphan reconcile covers it — a `Running`
run whose lease expired *or was never stamped* is flipped to `Failed` +
`RunbookRun.Interrupted`, and a live lease is never touched. Mid-wave agent
loss goes through §2's disconnect monitor and §1's wave deadline, not a
run-level ceiling.

The two B3-era reap modes this section used to define are gone with the
hand-off model itself: the *pre-hand-off* expired-lease reap folded into the
kind-agnostic orphan arm, and the *agent-owned* `RunbookRun.TimedOut` ceiling
(`Engine:MaxRunbookRunDuration`, reconciler arm 4) was a transition-era drain
for legacy pre-D1 hand-off runs, deleted in D1 Phase 3 together with the hub's
fallback finalize. `RunbookRun.TimedOut` remains in the audit vocabulary for
historical rows only.

The orphan flip is a conditional update (re-checks the predicate in the
`WHERE`) — fail-closed against racing a live owner — and emits an explicit
audit row (`ExecuteUpdate` bypasses the audit interceptor).

## 5. Blue-green drain (verified, no code change)

The gauge needed no change: the deadline/monitor unwind the dispatch method,
the `using`-scoped tracker disposes, and `ReleaseDrainDecision.ShouldRetire`
proceeds. Pinned by test: a silent agent's deployment goes terminal and the
gauge returns to 0.

## Configuration reference

```json
"Engine": {
  "MaxTargetWaveDuration":    "01:00:00",
  "MaxTargetQueueWait":       "02:00:00",
  "AgentDisconnectWaveGrace": "00:02:00"
}
```

## Acceptance & verification

- `DisconnectReconciliationTests` (orchestrator harness, real
  `DeploymentWorker`): hung agent hits the ceiling (gauge back to 0), explicit
  step timeout honoured, vanished agent cancelled after the grace, BestEffort
  survivors continue / Atomic fails farm-wide, retries abandoned when the
  agent stays offline (one grace window, not N deadline windows).
- `DispatchReconcileTests` (B3 additions): expired-lease runbook run failed +
  `RunbookRun.Interrupted`; overdue agent-owned run failed +
  `RunbookRun.TimedOut`; in-ceiling agent-owned run and live-lease run left
  alone.
- Late-completion idempotency after a deadline/disconnect cancel is covered by
  B2's `PendingSubPlanRegistryDispatchIdTests` (cancelled attempts retire
  their dispatch id).

## Known residuals (deliberate, tracked)

- **B6**: cooperative in-flight abort (`CancelDeploymentAsync`) — a cancelled
  or reaped wave still runs to completion agent-side; only the *server* stops
  waiting. `DispatchId` on `AppendLogAsync`, `ContractVersion`.
- **B7**: full retry re-resolution (variable re-snapshot, node concurrency
  cap, package-cache safety).
- **B5**: `xmin` optimistic concurrency across all status writers (the
  conditional-UPDATE guards here narrow, but do not eliminate, blind-write
  races between writers).

## References

- `docs/production-fix-prompts-2026-07-13.md` — B3 spec (§B3), B5/B6/B7
  boundaries.
- `docs/agent-reconnect.md` — B2: the reconnect + outbox + `DispatchId`
  machinery this builds on.
- `docs/durable-dispatch.md` — B1: lease/claim/reconciler this extends.
- `docs/blue-green-slot-deployment.md` §5/§9 — drain-and-retire rule.

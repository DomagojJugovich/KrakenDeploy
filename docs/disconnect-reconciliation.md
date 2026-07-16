# Disconnect Reconciliation & Wave Deadlines

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-16 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR, EF Core 10, PostgreSQL |
| **Projects** | KrakenDeploy.Server.Transport, KrakenDeploy.Server.Data, KrakenDeploy.Server |

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

## 4. Runbook-run reap (dispatch reconciler step 4)

Two distinct stranding modes, both DB-based so they survive restarts:

- **Pre-hand-off (`RunbookRun.Interrupted`)** — `Running` with an *expired*
  lease: the dispatching process died between the atomic claim and the
  `RunDeploymentAsync` push, so the plan never reached the agent. The
  reconciler's deployments-only orphan step deliberately skips runbook runs
  (a *released* lease is their normal agent-owned state); this is their
  equivalent for the claim-to-hand-off window. A live lease is never touched.
- **Agent-owned (`RunbookRun.TimedOut`)** — `Running`, lease released,
  `StartedUtc` older than `Engine:MaxRunbookRunDuration` (default 1 h): the
  completion callback never came and nothing else can ever finalize the row.
  Raise the knob for long maintenance runbooks. The B2 outbox delivers
  genuinely-in-flight completions across disconnects well inside a sane
  ceiling; a late completion after the reap is swallowed by the hub's
  `IsTerminal` guard.

Both flips are conditional updates (re-check status + lease in the `WHERE`) —
fail-closed against racing a live owner — and emit explicit audit rows
(`ExecuteUpdate` bypasses the audit interceptor).

## 5. Blue-green drain (verified, no code change)

The gauge needed no change: the deadline/monitor unwind the dispatch method,
the `using`-scoped tracker disposes, and `ReleaseDrainDecision.ShouldRetire`
proceeds. Pinned by test: a silent agent's deployment goes terminal and the
gauge returns to 0.

## Configuration reference

```json
"Engine": {
  "MaxTargetWaveDuration":    "01:00:00",
  "AgentDisconnectWaveGrace": "00:02:00",
  "MaxRunbookRunDuration":    "01:00:00"
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

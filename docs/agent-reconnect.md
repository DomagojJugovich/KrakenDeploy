# Agent Reconnect & Report Outbox

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-16 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR (WebSockets), Channels |
| **Projects** | KrakenDeploy.Agent, KrakenDeploy.Agent.Transport, KrakenDeploy.Contracts, KrakenDeploy.Server.Transport |

## Purpose

Covers the B2 batch (T0-2, plus B6.2 pulled forward): the agent's control
tunnel must survive for the life of the process. Before B2: the bare
`WithAutomaticReconnect()` gave up after four attempts (~40 s) and the `Closed`
handler only logged; `ServerLinkHostedService` parked on
`Task.Delay(Infinite)`; the initial `StartAsync` had no retry at all. Any
server restart, deploy, or blip longer than ~40 s therefore took the agent
offline **until its process was manually restarted** — and combined with
blue-green, a zero-downtime server upgrade could knock the whole agent fleet
permanently offline. Reports produced while disconnected (step logs, step and
deployment completions, adhoc results) were silently lost.

## Resilience posture (decided)

**The agent never stops trying, and work results are never dropped while the
process lives.** Reconnect pacing is unbounded with a jittered cap; report
delivery is *at-least-once*, made safe by a per-dispatch idempotency key the
server dedups on. Buffering is process-lifetime only — an agent that dies
mid-deployment is the server-side dispatch reconciler's story (B1), not the
transport's.

## 1. Unbounded reconnect pacing (`AgentReconnectPolicy`)

One `IRetryPolicy` paces every path: the connection's own automatic reconnect,
the supervisor's initial-connect retries, and its restart cycles.

- Attempt 0 retries immediately (rides out sub-second blips), then full-jitter
  exponential backoff — `random(0, min(30 s, 1 s · 2^(n−1)))` — forever.
  `NextRetryDelay` never returns `null` (never gives up); full jitter spreads a
  fleet's reconnect storm after a server restart.
- **Auth lane**: a 401/403 `RetryReason` (token revoked via the A8 `atv` claim,
  or expired past its refresh budget) switches to a fixed 5-minute cadence and
  logs *"an operator must re-enroll this agent"* once per streak. Still
  unbounded — the slow lane costs nothing and self-heals if the credential is
  restored. Detection is typed (`HttpRequestException.StatusCode` from the
  negotiate); an auth failure surfacing as another exception type just stays in
  the normal 30 s lane (noisier, not wrong).

## 2. Supervised link lifecycle (`ServerLinkHostedService`)

`WithAutomaticReconnect` never covers *initial* start failures (documented
SignalR behaviour), and a `Closed` event with an unbounded policy still fires
for closes automatic reconnect does not cover. The hosted service therefore
supervises:

- **Initial connect** retries with the policy's pacing — an agent booting while
  the server is down comes online by itself (pre-B2 the service died on the
  first failure).
- **Transient drops** are handled inside the `HubConnection` and never reach
  the supervisor.
- **Permanent closes** surface via the new `IServerLink.OnClosed` and restart
  the whole cycle with a fresh connection. `SignalRServerLink.StartAsync` is
  re-entrant (disposes the previous connection; a deliberate-stop flag plus a
  current-connection reference guard keep replaced/stopped connections from
  triggering spurious restarts).
- **Registration** is re-sent best-effort on every connect and `OnReconnected`
  (the hub's `OnConnectedAsync` re-marks the target Online on every physical
  reconnect regardless — registration only refreshes machine info).
- **Clean shutdown** still reports `ShuttingDown` and stops deliberately.
- **No zombie mode**: the supervision loop has no broad catch. An unexpected
  supervisor crash stops the host (`BackgroundServiceExceptionBehavior.StopHost`)
  so service-manager recovery restarts the agent — a visible crash-loop beats a
  silently dead link.

`HeartbeatHostedService` is unchanged: it already observes `IsConnected` on a
30 s `PeriodicTimer` (no spinning), transport keepalive detects dead links, and
the policy owns retry pacing — there is nothing left for the heartbeat to
drive.

## 3. Report outbox (`ServerLinkOutbox`)

`AppendLog`, `ReportStepCompleted`, `CompleteDeployment` and
`ReportAdhocResult` no longer invoke the hub directly. Callers enqueue into a
single unbounded channel and return; one pump task delivers strictly FIFO over
the *current* connection, waits (1 s poll) while disconnected, and retries an
item until the server acks it.

- **Ordering**: one pump + the server's sequential per-connection dispatch
  (`MaximumParallelInvocationsPerClient` default 1) guarantees a wave's step
  reports are acked before its completion goes out — the order the
  orchestrator's drain contract needs.
- **Bounds**: completions/adhoc results are bounded by plan size and never
  dropped. Log lines cap at 5 000 queued; beyond that the newest is dropped,
  counted, and warned locally (the agent's rolling log file retains
  everything; server transcripts get a hole only in pathological outages).
- **Poison safety**: 5 consecutive failures on a *stable* connection drop the
  item (error-logged) so the queue can never wedge; a disconnected wait resets
  the counter. `HubException` takes the same capped path — a transient hub-side
  fault gets retried, a deterministic rejection drops after ~5 s.
- `Register`/`Heartbeat`/`ReportStatus` stay direct sends: stale ticks are
  junk, and the shutdown status is best-effort by design.

## 4. Per-dispatch idempotency (`DispatchId`) — CONTRACT CHANGE

At-least-once delivery makes duplicates and lates *normal*, and completion was
keyed only by `(deploymentId, targetId)` — not unique per message: wave
retries re-dispatch under the same key (a stale completion could resolve the
new attempt's TCS — pre-existing T1-3), and a duplicate fell through the
consumed TCS slot to the hub's DB fallback, guarded only by `IsTerminal` —
mid-orchestration the parent is *not* terminal, so a dup could finalize a
deployment whose later waves were still running.

- `DeploymentPlan.DispatchId` (appended, defaulted): `DeploymentWorker` stamps
  a fresh GUID per dispatch **attempt**; `RunbookRunWorker` stamps at
  construction (uniformity — runbook completions still take the fallback
  finalize path by design, deduped by `IsTerminal`).
- The agent echoes it in `CompleteDeploymentAsync` and
  `ReportStepCompletedAsync` (hub methods gained the parameter — **agents and
  server must be redeployed together**; pre-production, no external agents).
- `PendingSubPlanRegistry` keys the slot by it and keeps a bounded (16 384)
  process-lifetime set of retired (resolved/cancelled) ids.
  `RouteCompletion` replaces `TryResolve` with a tri-state:
  `ResolvedPending` (orchestrator continues), `StaleOrDuplicate` (swallowed,
  warn-logged), `NoPendingSubPlan` (DB fallback — runbook runs, post-restart
  lates; both `IsTerminal`-guarded). `RecordStepResult` drops stale attempts'
  step reports so a retry's attribution bag stays clean. `Guid.Empty` preserves
  legacy match-by-slot behaviour (offline-era plans) and is never retired.
- After a server restart the retired set is empty — but the TCS is gone too,
  and the B1 reconciler + `IsTerminal` guard own the outcome. No DB table
  needed.

## Acceptance & verification

- `AgentReconnectPolicyTests` — unbounded (attempt 100 000), cap + jitter
  bounds, immediate first retry, 401/403 slow lane + recovery.
- `ServerLinkHostedServiceTests` — initial-connect retry, restart on permanent
  close, re-registration on reconnect, registration failure non-fatal, clean
  shutdown.
- `ServerLinkOutboxTests` — FIFO across kinds, buffer-and-flush, at-least-once
  retry, capped poison drop (incl. `HubException`), log cap, prompt shutdown.
- `PendingSubPlanRegistryDispatchIdTests` — dup swallowed, stale attempt can't
  resolve a newer attempt, cancelled-attempt late swallowed, unknown falls
  back, legacy `Guid.Empty` semantics, stale step reports dropped.
- `ReconnectE2ETests` — the REAL `SignalRServerLink` against a Kestrel hub
  stopped and restarted on the same port: auto-reconnect without process
  restart, `OnReconnected` re-registration, buffered reports flushed FIFO with
  the `DispatchId` intact.
- Host boot checks: server (Development, DI validation) and agent (constructs
  all services; found and fixed a double-dispose on the DI shutdown path).

**Manual acceptance (not CI-runnable)**: (1) stop the server for 5 minutes
with an idle agent, restart it — the agent reconnects and shows Online without
a process restart; (2) blue-green slot swap under load — the fleet reconnects
through the router (the `X-KD-Release` pin rides every reconnect from the
connection options; a stale pin falls back to the default release).

## Known residuals (deliberate, tracked)

- **B3 — LANDED 2026-07-16** (`docs/disconnect-reconciliation.md`): wave
  deadlines, the mid-wave disconnect monitor (grace tuned to this outbox's
  flush window), and the runbook-run reap. B2's buffered completions make the
  post-outage completion *arrive*; B3 makes the server give up *waiting* when
  it should.
- **B6** (pre-freeze wire pass) still owes: `DispatchId` on `AppendLogAsync`
  (log-line attempt attribution), `CancelDeploymentAsync` (cooperative abort),
  `ContractVersion` negotiation.
- A late `AppendLogAsync` for an already-compacted step leaves staging rows
  uncompacted until the task's terminal sweep; harmless, noted for B3.
- `TokenRefreshHostedService` keeps its 6 h check cadence; it does not re-check
  immediately after a reconnect (45-day refresh budget makes this irrelevant).

## References

- `docs/production-fix-prompts-2026-07-13.md` — B2 spec; B6.2 (pulled forward
  here); B3/B5 coordination notes.
- `docs/durable-dispatch.md` — B1 lease/reconciler this builds on.
- `docs/blue-green-slot-deployment.md` §3 — the `X-KD-Release` pin.
- [ASP.NET Core SignalR .NET client — Handle lost connection](https://learn.microsoft.com/aspnet/core/signalr/dotnet-client?view=aspnetcore-10.0#handle-lost-connection)
  — automatic reconnect does not cover initial start failures; `IRetryPolicy`
  contract.

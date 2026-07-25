# Node Concurrency & Cache Safety (B7)

| | |
|---|---|
| **Version** | 1.2 |
| **Date** | 2026-07-25 |
| **Authors** | Domagoj Jugović, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR, SemaphoreSlim, NTFS/ext4 atomic rename |
| **Projects** | KrakenDeploy.Server.Transport, KrakenDeploy.Agent, KrakenDeploy.Agent.Transport |

Production-readiness fix **B7** (audit items T1-3 remainder, T1-4): bounded
concurrency on both sides of the wire, and caches that survive crashes and
concurrent access. No contract change; one new config key.

> v1.2 (2026-07-25, F2-followup 3): the ad-hoc bound is now ONE
> `Adhoc:MaxTotalDuration` covering queue wait **plus** execution, replacing the
> queue-only `Adhoc:MaxQueueWait` — see "Ad-hoc scripts take the slot too".
>
> v1.1 (2026-07-25, F2): the agent's execution slot moved out of
> `DeploymentExecutor` into the shared `MachineExecutionGate`, ad-hoc scripts
> now take it, and `DeploymentTarget.AllowParallelTaskExecution` opts a target
> out.

## Server: the node task cap

`Engine:MaxConcurrentTasks` (Octopus-parity default **5**; non-positive falls
back) bounds how many deployment orchestrations run concurrently per node.
Pre-B7 the worker fire-and-forgot every dequeued item — an enqueue burst ran
unbounded concurrent orchestrations, each holding DB contexts, a log sequencer
and per-target dispatch state for its whole duration.

`NodeTaskGate` is a FIFO semaphore; excess items wait inside their
fire-and-forget task holding nothing else. The slot is deliberately taken
**before** the blue-green in-flight gauge: a queued-but-unstarted deployment
is still `Queued` in the database, so if a draining slot retires first, the
B1 claim + boot reconciler hand the item to the survivor — it must not block
the drain. A shutdown while queued likewise just leaves the row `Queued`.

Runbook-run dispatch is **not** counted: the server-side hand-off is
milliseconds (the run executes on the agent, where the machine queue below
bounds it).

## Agent: one task at a time per machine

Chosen fork (confirmed): the machine queue lives **agent-side**, not as
server-side target locks — server locks are per-process, so a blue-green
overlap would bypass them, and multi-target deployments would need ordered
multi-lock acquisition. The agent is the single authority for its own box.

The slot itself is `MachineExecutionGate`, a process-wide agent singleton (F2
extracted it out of `DeploymentExecutor` so more than one caller can share it).
A holder keeps it for one dispatched sub-plan — i.e. one WAVE (see Residuals):
concurrent deployments, runbook runs **and ad-hoc scripts** to the same box
serialize FIFO instead of interleaving file/IIS/service mutations; waves
*within* a plan keep their parallelism. A queued plan writes
`--- Waiting for another task to finish on this machine ---` to its task log.

The gate is only reachable because the agent's SignalR push handlers are
**detached** (`ServerLinkHostedService` returns `Task.CompletedTask`, not the work
task). The client awaits each client-method handler, so returning the work task
made the agent process one push at a time — the transport, not the gate, did the
serializing, and B7's queue, F2's per-target flag, the B6 supersede path and the
cancel push were all unreachable. Pinned by
`ServerLinkHostedServiceTests.Deployment_push_handler_returns_without_awaiting_the_run`
and three real-hub tests in `TransportRoundTripTests`.

Registration in the B6 single-flight registry happens **before** queueing, so
a queued plan stays cancellable and supersedable — the wait observes the run's
token, and a cancel lands in the aborted-completion path with nothing
executed. `AgentUpdateService`'s is-it-safe-to-swap gate covers queued plans
too (registry-derived since B6). It does **not** cover a running ad-hoc script
(pre-existing gap; ad-hoc runs are not in `_running`).

A plan QUEUED on the gate also observes the host's shutdown token, so it unwinds
its registry entry and staging at shutdown instead of parking on a disposed
semaphore (`SemaphoreSlim.Dispose` does not signal pending waiters). Step
execution is deliberately NOT linked to shutdown — a disconnect or shutdown must
never abort a step that is already running.

### Ad-hoc scripts take the slot too (F2)

Before F2 ad-hoc scripts bypassed the gate outright — "deliberate: they are
operator-interactive diagnostics" — which meant an approved diagnostic script
could run straight into a deployment's file / IIS / service operations. They now
queue like everything else, with one difference: the whole thing is **bounded**
by a single `Adhoc:MaxTotalDuration` budget (default 5 min) measured from the
moment the command arrives. Expire while queued and the agent REFUSES with an
`AgentError`; expire while running and it kills the process tree and reports the
timeout.

The budget spans BOTH phases on purpose. Separate wait and run bounds looked
tidier but did not deliver the property they claimed: a 3:59 queue plus a 5:00
run still outlived the server's per-target ad-hoc wait
(`AdhocDispatcher.DefaultTimeout`, 5 min), so a script the dispatcher had already
resolved as "timed out" could execute — and mutate the box — afterwards, while an
operator who saw the timeout and approved a fresh iteration got both. One budget
equal to the dispatcher's timeout is what actually closes that window. The two
ends are coupled by intent, not by the wire: raise the config knob if you raise
the dispatcher timeout. Values that are unparseable, non-positive, or over 24 h
warn and fall back to the default — note a bare number parses as DAYS.

### Per-target opt-out (F2)

`DeploymentTarget.AllowParallelTaskExecution` (Octopus "Allow parallel task
execution", default **off**) is stamped into `DeploymentPlan` /
`AdhocScriptCommand` at dispatch time; the agent bypasses the gate for that unit
of work. It is per **target**, so one machine's opt-in cannot leak onto another
in the same fan-out, and a flip applies to the next dispatch, not to work already
queued on the agent. It never relaxes the F1 same-`(project, environment,
tenant)` deployment serialization — that is enforced server-side at claim time
and has no per-target opt-out.

### Queueing no longer burns the wave deadline (F2)

It used to: a wave dispatched to a busy agent spent its whole server-side B3
deadline queued. The agent now reports gate acquisition
(`IAgentHubServer.ReportExecutionStartedAsync`) and the server arms the wave
budget from that point; the dispatch-time arm becomes a backstop
(`budget + Engine:MaxTargetQueueWait`, default 2 h) so a wedged agent that never
reports is still reaped. See `docs/execution-engine.md` §6.

## Package cache: existence == completeness

`LocalPackageCache` pre-B7: the store truncated the final path in place
(`FileMode.Create`), so a concurrent reader saw a torn zip, and an agent
crash mid-copy left a **permanently poisoned** entry every later hit returned
(the doc comment claimed OS-level serialisation — it was wrong).

Now a cache entry only ever exists complete:

- `StoreAsync` copies into a unique `.tmp-*` sibling and atomically renames
  into place (same directory ⇒ same volume ⇒ atomic move). A crash leaves
  only orphaned tmp files, never a bad final entry.
- `TryGetCachedPath`'s existence check therefore doubles as the completion
  marker. Content integrity stays the **downloader's** job — it SHA-256
  verifies full transfers (always, since B6) and Octodiff verifies deltas
  before `StoreAsync` is ever called, so re-hashing per hit would buy nothing.
- Stores of the same `(packageId, version)` are single-flighted per key, and
  a store that finds the entry present **keeps it** (entries are
  content-addressed; replacing would also race a concurrent extraction
  holding the zip open — Windows refuses to replace an open file). This is a
  semantics change: re-store no longer refreshes an entry.

`StepPackageLoader.ExtractToCache` gets the same discipline: extract into a
unique sibling dir, `Directory.Move` into place, reuse a completed dir
(versions are immutable + verified; a live swap is impossible anyway — the
loaded ALC locks its assemblies; repairing a suspect entry = uninstall +
reinstall). Pre-B7 it deleted + extracted in place, with the same torn/poison
failure modes against the loader's bare `Directory.Exists` hit.

## Server-side script kill

`ServerScriptStepRunner` now kills the spawned process **tree** (+10 s reap)
when its wait is cancelled — every per-step timeout and deployment cancel
used to leak an orphan shell that kept mutating server-side state. Mirrors
the agent's B6 `ScriptRunner` kill and closes the residual B4/B6 both noted.

## Already done elsewhere (spec scope absorbed by earlier WPs)

- Retry connection re-resolve (H2 in the audit): B3 refreshes
  `GetConnectionId(targetId)` per attempt and treats "no connection" as
  abandonment, not send-to-void.
- Racy `IsExecuting` bool: B6 replaced it with the running-task registry.

## Tests

`NodeTaskGateTests` (hard bound under 12 workers, default fallback,
idempotent releaser, cancellable wait); `DeploymentExecutorCancelTests`
(different tasks serialize FIFO; cancel-while-queued aborts without
executing); `LocalPackageCacheTests` (concurrent store+read never torn,
tmp-* invisible, keep-on-restore); `StepPackageLoaderTests` (6 concurrent
extractions → one complete dir, no temp survivors);
`ServerScriptStepTimeoutTests` (the timed-out script's PID actually dies).

## Residuals

- The node cap bounds **orchestrations**, not queued channel depth — a
  million queued deployments still consume channel memory (bounded by
  real-world usage; the DB is the durable queue).
- The gate is per AGENT PROCESS, not per physical machine: two targets modelled
  on one box are two processes, two gates, and no serialization between them.
  Nothing enforces `MachineName` uniqueness. The offline runner likewise builds
  its own gate (deliberately uncoordinated with a live agent).
- The gate's unit is the dispatched sub-plan, i.e. one WAVE — the server
  dispatches per wave, so the slot is released and re-taken at each wave
  boundary. An ad-hoc script can therefore still slot in between two waves of
  one deployment.
- No per-target fairness across agents: the cap is node-global, FIFO.

## References

- `docs/production-fix-prompts-2026-07-13.md` — B7 work package
- `docs/disconnect-reconciliation.md` (B3), `docs/agent-wire-contract.md` (B6)

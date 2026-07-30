# Node Concurrency & Cache Safety (B7)

| | |
|---|---|
| **Version** | 1.5 |
| **Date** | 2026-07-30 |
| **Authors** | Domagoj Jugović, Claude (Opus 4.8), Claude (Opus 5) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR, async reader-writer lock, NTFS/ext4 atomic rename |
| **Projects** | KrakenDeploy.Server.Transport, KrakenDeploy.Agent, KrakenDeploy.Agent.Transport |

Production-readiness fix **B7** (audit items T1-3 remainder, T1-4): bounded
concurrency on both sides of the wire, and caches that survive crashes and
concurrent access. No contract change; one new config key.

> v1.5 (2026-07-30, F5 review round 3): the shared-holder clamp now LOGS a warning
> naming the effective cap (it was silent, while the code claimed otherwise). The
> self-upgrade's deferral report moved OUT of the machine lease and carries the real
> reason from the check rather than a fixed string, and it is filed once per staged
> version instead of once per check interval. `DeploymentExecutor`'s wedged-gate wait is
> now DERIVED from `Agent:Update:SwapGateTimeout` — a flat 30 s was shorter than the
> default swap window, so a healthy box could be reported as wedged — and its escalation
> states what was observed instead of diagnosing a stuck predecessor.
>
> v1.4 (2026-07-29, F5 review follow-up): shared co-running is BOUNDED by
> `Agent:MaxConcurrentSharedWork` (default 8) — the reader-writer rework had removed
> the old hard cap of one with nothing in its place. The self-upgrade now asks the
> SERVER whether a task is still assigned to this target before swapping (the gate's
> unit is one wave, so an idle gate does not mean an idle plan), re-checks the
> maintenance window after queueing, decides its permanent refusals BEFORE taking the
> gate, reports a deferred swap, and puts the ROLLBACK swap under the write lock too.
> `Agent:Update:*` durations are validated at startup (`SwapGateTimeout < CheckInterval`
> is ENFORCED; staying under `Adhoc:MaxTotalDuration` is a property of the DEFAULTS, not
> a rule) and `SwapGateTimeout` now defaults to 2 min.
>
> v1.3 (2026-07-29, **F5**): the agent's machine gate is now a fair async
> READER-WRITER lock, not a binary mutex. `AllowParallelTaskExecution` selects a
> SIDE instead of granting a bypass (mutual consent — locked decision P2), for
> ad-hoc it became per-RUN rather than per-target, and the agent self-upgrade
> takes the WRITE side (locked decision P8). CONTRACT CHANGE:
> `AgentContract.CurrentVersion` 2 → 3 with no shape change. See "Agent: the
> machine execution gate" and "The self-upgrade participates too".
>
> v1.2 (2026-07-25, F2-followup 3): the ad-hoc bound is now ONE
> `Adhoc:MaxTotalDuration` covering queue wait **plus** execution, replacing the
> queue-only `Adhoc:MaxQueueWait` — see "Ad-hoc scripts take the gate too".
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

## Agent: the machine execution gate

Chosen fork (confirmed): the machine queue lives **agent-side**, not as
server-side target locks — server locks are per-process, so a blue-green
overlap would bypass them, and multi-target deployments would need ordered
multi-lock acquisition. The agent is the single authority for its own box.

The gate itself is `MachineExecutionGate`, a process-wide agent singleton (F2
extracted it out of `DeploymentExecutor` so more than one caller can share it).
Since **F5** it is a fair asynchronous **reader-writer** lock with two modes, not a
binary mutex — Octopus `ScriptIsolationMutex` parity, verified from Tentacle source:
theirs is an in-process `AsyncReaderWriterLock` acquired per script, and its
`NoIsolation` option takes the READ side of that same lock. "Bypass" is a downgrade
to shared, never an actual bypass.

| Mode | Who takes it | Co-runs with |
| --- | --- | --- |
| **EXCLUSIVE** (write) | default for a dispatched sub-plan; an ad-hoc command with `AllowParallelTaskExecution = false` (WP16 console default); the self-upgrade swap window | nothing |
| **SHARED** (read) | a sub-plan whose target sets `AllowParallelTaskExecution`; an ad-hoc command with the flag `true` (the AI session flow, always) | other SHARED holders only |

Co-running requires that **no writer is present**, in either direction, so consent is
MUTUAL (locked decision P2): one side opting into sharing cannot force the other to.
A holder keeps its lease for one dispatched sub-plan — i.e. one WAVE (see Residuals);
waves *within* a plan keep their parallelism. A queued plan writes
`--- Waiting for other work to finish on this machine ---` to its task log
(`--- Waiting for exclusive work … ---` when it is itself shared).

**Fairness is load-bearing.** Acquisition never barges past a queued waiter even when
the gate's current state would permit it: without that rule a steady stream of shared
ad-hoc scripts would keep the reader count above zero indefinitely and a queued
deployment — or the self-upgrade — would never be granted. This is why the primitive is
hand-built: `ReaderWriterLockSlim` has no async surface at all, and the usual
`SemaphoreSlim` recipes either barge or need a second lock to stay consistent. A
bounded wait that expires, or a cancel while queued, leaves the queue clean and holds
nothing — the classic hand-rolled-lock bug is a timed-out waiter still in the queue
being "granted" a lease nobody then releases.

Fairness has a consequence worth knowing when reading logs: **a queued writer blocks
acquisition even with no holder present.** So "the machine was not available" does not
imply "something was running" — it may have been the agent's own self-upgrade waiting
its turn, or the shared-holder cap below. The ad-hoc refusal message is worded to
avoid naming a holder for exactly that reason, but it does not name the self-upgrade
either: if a script was refused on a box that looks idle in the task history, check
that agent's own log for a queued swap.

**Shared co-running is bounded** by `Agent:MaxConcurrentSharedWork` (default 8, clamped
to `[1, 64]`). The pre-F5 primitive was a `SemaphoreSlim(1, 1)`, i.e. a hard cap of ONE
unit of work per machine; reader-writer semantics removed that bound and nothing else
replaced it — the ad-hoc path is shared-always, the agent's SignalR push handlers are
detached with no limiter, and no server-side cap covers ad-hoc dispatch, so N concurrent
approvals meant N PowerShell processes. Octopus's own reader-writer lock is uncapped, so
this is a deliberate divergence.

An out-of-range value is clamped rather than refused — a `0` would make every shared
acquisition permanently unsatisfiable, which is a worse outcome for a config typo than
narrowing the cap — but the clamp **logs a warning naming the effective value** at agent
startup. It did not, and silence was the wrong answer in both directions: an operator who
sized a box for 200 co-running scripts ran at 64 with nothing in any log, and a typo'd `0`
silently serialized the entire ad-hoc path while the per-script refusal message blamed the
machine rather than the cap. Setting it to `1` is a legitimate way to serialize approved
AI actions, which the target page's callout now says instead of claiming no such setting
exists.

The cap is soft **at the gate** — over-cap work queues rather than being refused — but
not end to end: an ad-hoc script's `Adhoc:MaxTotalDuration` budget spans its queue wait,
so a script that queues behind the cap for its whole budget IS refused. The bound is
placed at the gate rather than where work is spawned (the detached push handlers, or
server-side ad-hoc dispatch) because the gate is the one chokepoint every kind of work
already passes through; the honest cost is that the refusal reaches the operator late,
from the agent, rather than at submit time. `MachineExecutionGate.WouldAdmitConcurrently`
exists so callers reason about co-running by ASKING the gate — the cap means "both
shared" no longer implies "both admitted", and `DeploymentExecutor`'s same-task refusal
depends on getting that right.

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
executed.

A plan QUEUED on the gate also observes the host's shutdown token, so it unwinds
its registry entry and staging at shutdown instead of parking forever. Since F5 the
gate additionally fails every queued waiter on its own `Dispose` (the old
`SemaphoreSlim` did not signal pending waiters at all, which is why the linked token
was mandatory rather than belt-and-braces). Step execution is deliberately NOT linked
to shutdown — a disconnect or shutdown must never abort a step that is already
running.

### The self-upgrade participates too (F5, locked decision P8)

`AgentUpdateService` used to gate the binary swap on `DeploymentExecutor.IsExecuting`
alone. The 2026-07-25 parallel-safety audit found two defects in that: `IsExecuting` is
derived from the deployment registry and is **blind to ad-hoc scripts**, so a
maintenance-window swap killed an operator's running diagnostic; and the gap between
reading it and moving the directory was a **TOCTOU** that work could start inside.

The swap window (extract + directory swap + `Environment.Exit`) now runs under the
gate's EXCLUSIVE side, which every kind of work participates in. Because the gate is
writer-fair, a *queued* updater already blocks new work from starting — that is the
guarantee wanted, and precisely why the wait is **bounded** by
`Agent:Update:SwapGateTimeout` (default 2 min): an unbounded one would let a wedged
holder stop the agent from accepting work for the rest of the process's life. On expiry
nothing is swapped, the outcome is reported as `swap-deferred` so a machine that keeps
deferring is visible server-side, and the next tick retries. `IsExecuting` is kept purely
as a cheap early-out, so the agent does not pay the block-new-work cost when a deployment
is already known to be running.

The lease is deliberately **not** released on the success path — the process exits holding
it, because releasing it after the directory has been replaced would hand the machine to a
queued deployment that then gets hard-killed mid-step by the exit. That makes
`Environment.Exit` the *only* thing that ends the lease, so it sits in a `finally`: the
best-effort outcome report on the way out threw an `HttpClient` timeout (a
`TaskCanceledException`, raised even on `CancellationToken.None`), the exit was skipped, and
the gate was left with a writer held by nobody for the life of the process — every later
wave parking on it forever while the target heartbeated Online.

The **rollback** swap takes the same lock, with a short fixed wait that does *not* abandon
on expiry: restoring a binary that failed its health gate outranks exclusivity, so it
proceeds ungated (and says so in the log) rather than leaving a bad build running. A
SHUTTING-DOWN host is the one case that defers instead — the marker is retained and the
next boot retries the whole probation. Both halves of that matter: there the lease is
guaranteed *absent* rather than merely contended, and a shutdown deliberately does not abort
a running step; and `Environment.Exit(70)` during an intentional stop reports failure to the
supervisor, so a service with `FailureActions`, a `Restart=on-failure` unit or a container
with the same restart policy relaunches the agent the operator just stopped.

Deferrals are reported **once per staged version**, not once per check. The causes are
open-ended — a task parked `Queued` by maintenance mode, or sitting at
`PendingOfflineResult` — and reporting each tick filed ~24 audit rows per target per night,
each of which `SubscriptionMatcher` also delivers to any subscription with an empty pattern
list. The reported reason comes from the check itself rather than a fixed string: it fails
closed on a 5xx, an unparseable body, a transport error and its own timeout, and an audit
row claiming "a task is assigned" when the truth was "the server did not answer" sends an
operator looking for a task that does not exist.

Holding the gate is **not sufficient on its own**, and this is the sharpest thing to
understand about the updater. The gate's unit is one WAVE, so between two waves of a live
multi-wave deployment the gate is free *and* the in-flight registry is empty — and a
SERVER wave (a manual intervention, a `DeployRelease` cascade) can sit in that gap for
minutes or hours. A swap there would `Environment.Exit(0)` into a half-applied box, and
writer fairness makes it *win* that boundary deterministically once the check lands in a
gap. So immediately before extracting, the agent asks the server
`GET /api/agents/task-in-flight` — the only party that sees whole plans — and defers
unless the answer is a clear "idle". **Fail-closed:** an unreachable server, a non-success
status or an unparseable body all defer. Two other ordering rules follow from the same
window: permanent refusals (not the apphost, no resolvable install dir) are decided
BEFORE the gate is taken, because an agent that can never swap must not freeze the
machine on every tick forever; and the maintenance window is re-checked AFTER the queue
wait, because C6's invariant is that a swap happens inside the approved window, not
merely that it was inside one when the tick began.

`Agent:Update:*` durations are validated at startup (`AgentUpdateConfigValidator`,
`ValidateOnStart`), mirroring `EngineOptionsValidator`. This is not ceremony:
`SwapGateTimeout` is the bound that stops a wedged holder from starving the agent, and
every malformed value defeats it — `"5"` binds as five DAYS, `"-00:00:00.001"` IS
`Timeout.InfiniteTimeSpan` so the wait is never bounded at all, and `"00:00:00"` degrades
the acquisition to a non-blocking probe. The validator also enforces
`SwapGateTimeout < CheckInterval`, because at equal values (the original 5/5 pair)
`PeriodicTimer` has a coalesced tick ready the instant the wait expires, so the updater
re-queues a machine-blocking writer back-to-back for the whole window without ever
completing a swap. The default is 2 min, which also keeps it below
`Adhoc:MaxTotalDuration` so a script queued behind the updater is not guaranteed to lose
its budget.

### Ad-hoc scripts take the gate too (F2/F5)

Before F2 ad-hoc scripts bypassed the gate outright — "deliberate: they are
operator-interactive diagnostics" — which meant an approved diagnostic script
could run straight into a deployment's file / IIS / service operations. They now
take it like everything else, on the side `AdhocScriptCommand.AllowParallelTaskExecution`
selects, with one difference: the whole thing is **bounded**
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

### The parallel flag selects a side, it is not an opt-out (F2 → F5)

`DeploymentTarget.AllowParallelTaskExecution` (Octopus "Allow parallel task
execution", default **off**) is stamped into `DeploymentPlan` at plan-build time. Under
F2 the agent **bypassed** the gate for that unit of work; under F5 it takes the SHARED
side instead. The difference matters: a bypass removed same-machine protection against
*every* task on the box, including ones that had not opted in, whereas a shared lease
still queues behind any exclusive holder. Mutual consent, in both directions. It is per
**target**, so one machine's opt-in cannot leak onto another in the same fan-out, and a
flip applies to the next dispatch, not to work already queued on the agent. It never
relaxes the F1 same-`(project, environment, tenant)` deployment serialization — that is
enforced server-side at claim time and has no per-target opt-out.

For ad-hoc work the same wire field is **per-run, not per-target** (F5). F2 stamped each
target's own flag onto `AdhocScriptCommand`, which was right while the flag meant
"bypass" — a machine-local policy — and inverts the intent once it means "which side":
a serial target would have promoted an LLM-generated, gate-checked, operator-approved
read-only diagnostic into an EXCLUSIVE holder blocking live deployments. So:

- the **AI ad-hoc session flow always sends `true`** (locked decision P5 — read-always,
  never excludes);
- **WP16's script console** maps its per-run "allow running concurrently with other
  scripts" checkbox onto it, unchecked (the default) → `false` → EXCLUSIVE, because a
  hand-written script has no mode gate and its author is exactly the person not thinking
  about cross-task clashes.

The flag still rides OUTSIDE the ad-hoc signature binding — it is an
execution-serialization hint, not an authorization input.

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
idempotent releaser, cancellable wait); `MachineExecutionGateTests` (F5 — the gate
primitive: shared∥shared co-run, exclusion in BOTH directions, a queued writer not
starved by ten late readers, `TryAcquireNowAsync` not barging, expired/cancelled
waiters leaving nothing held, disposal unblocking queued waiters);
`MachineExecutionGateSharingTests` (F2/F5 — the three call sites over ONE gate:
ad-hoc∥ad-hoc, exclusive ad-hoc excluding a shared one, a shared ad-hoc still waiting
behind an exclusive deployment, and the updater both waiting for ad-hoc work
`IsExecuting` cannot see and blocking new work while it holds the swap window);
`DeploymentExecutorCancelTests` (different tasks serialize FIFO; cancel-while-queued
aborts without executing; parallel-flagged tasks co-run but still wait behind an
exclusive one); `TransportRoundTripTests` (the same two over a REAL hub);
`AgentHubRegisterTests` (a v2 agent is refused — the F5 skew is invisible on the wire);
`LocalPackageCacheTests` (concurrent store+read never torn,
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
  dispatches per wave, so the lease is released and re-taken at each wave
  boundary. An ad-hoc script can therefore still slot in between two waves of
  one deployment. F5 does **not** close this (`MachineExecutionGate` is deliberately
  not the place to hold a lease across a server round-trip); **F6** does, with
  server-side per-plan target exclusion at claim time. The wave-gap for ad-hoc work
  specifically is an ACCEPTED risk — locked decision P5, no dispatch-time check.
- No per-target fairness across agents: the cap is node-global, FIFO.
- The reader-writer modes are **type-blind**, exactly as Octopus's are: nothing on the
  wire says "this is a deployment" vs "this is a script". Mode comes only from the
  consent flags, so two mutually-consenting SHARED units co-run even if one is a
  full deployment.
- Because a gate serves ONE target, every deployment through it carries the same
  `AllowParallelTaskExecution`, so mutual consent between two *deployments* is
  degenerate — the mixed-mode pairs that actually occur are ad-hoc-vs-deployment and
  updater-vs-anything. Ad-hoc-vs-ad-hoc is the only pair F5 newly lets co-run on a
  default box, and it includes **mutating vs mutating**: an ACCEPTED RISK, see
  `docs/adhoc-actions.md` §2.
- The updater's server-side idle check closes the wave-boundary hole for the SELF-UPGRADE
  only. Other work can still slot between two waves of one plan (P5 accepts this for
  ad-hoc); **F6** closes it generally, server-side at claim time.

## References

- `docs/production-fix-prompts-2026-07-13.md` — B7 work package
- `docs/master-plan-2026-07-18.md` §5 — locked decisions P2 (mutual-consent
  reader-writer), P5 (ad-hoc), P8 (updater); F5/F6 work packages
- `docs/disconnect-reconciliation.md` (B3), `docs/agent-wire-contract.md` (B6/F5)

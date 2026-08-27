# Blue-green slot-based deployment (additive changes) — design note

| | |
|---|---|
| **Status** | `Approved` — implemented 2026-07-02 (registry + per-node router + telemetry + CLI orchestration + drain-watcher + agent pin echo); smoke-verified end-to-end. **Revised 2026-08-27 (BG1):** available ON-PREM (`Deployment:Topology=OnPremBlueGreen`) — see [§12 On-prem blue-green](#12-on-prem-blue-green-bg1); **D-bg-5 superseded** by T1/T2 (master plan §5, 2026-07-28). See [Implementation notes](#implementation-notes). |
| **Applies to** | Blue-green topologies: `Saas` (pooled app tier — any slot serves any account) and `OnPremBlueGreen` (single tenant, 1..N nodes, 3 slots per node). `OnPrem` uses neither slots nor the router. |
| **Scope** | Routine **additive** migrations. Non-additive changes: on-prem BG uses the `[StopTheWorld]` marker + stop-the-world runbook (§12); SaaS breaking changes use the per-shard `UPGRADING` + queue-quiesce + straddle-release path (see the self-upgrade section) — this scheme carries those releases but does not provide their schema safety. |
| **Render mode** | Interactive Server. Monolith (Blazor Server UI **+** orchestrator + Hangfire) is versioned as a single unit — **not** decoupled. |

## 1. Intent

Roll out a new release without dropping a single **live circuit** or **live deployment**. Old work finishes on the release that started it; new sessions, jobs, and agent connections go to the new release; the old release drains and is retired. Pooled compute is preserved — a slot is a *version*, not an account or node binding.

## 2. Model

Three fixed **slots** (`slot1`, `slot2`, `slot3`) are permanent infrastructure. Each slot is one full monolith instance per node (UI + orchestrator + Hangfire) running one **release**. Releases rotate through the slots round-robin; the slots themselves never change, so the front-end routing config is static.

Why three: at any moment you need the *current default* + the *previous, still draining* + *one drained and ready for the next deploy*. The right number is `(longest live-work lifetime ÷ deploy cadence) + 1`; three covers one drain overlap. If deployments routinely outlive your deploy interval, add slots.

A **release** is one build deployed to one slot. It moves through states: `Deploying → Active → Draining → Retired`. Exactly one release is the `current_default_release` at a time.

## 3. The cookie

- Name `__Host-kd_ver`; value is an **opaque `release_id`** (not a raw slot number, so it stays unambiguous even while a slot's deploy is mid-rollout across nodes). Attributes: `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`. The `__Host-` prefix binds it to the exact origin — correct, because version pinning is per-account (each account is its own subdomain).
- **Issued by the router** (YARP — see §6) when a request arrives with *no* cookie or a cookie whose release is `Retired`/unknown: the router routes to the `current_default_release` slot **and** adds `Set-Cookie: __Host-kd_ver=<current_default_release>`. Requests carrying a live cookie are routed by it with no `Set-Cookie`.
- **No signing needed.** Every live release is schema-compatible (additive), so a tampered or stale value at worst maps to the default. The pin is a convenience for not-dropping-work, not a security boundary.
- **Agents** (non-browser) use the same pin via an `X-KD-Release` header echoed on the persistent connection; a (re)connect with none routes to the default. A persistent agent connection naturally stays on its slot for its lifetime.

## 4. Catalog additions (control-plane, not per-account)

```sql
-- Release registry (control-plane scope; ~one row per known release)
CREATE TABLE release (
    release_id     text PRIMARY KEY,           -- opaque id carried in the cookie
    label          text NOT NULL,              -- human label / build number
    slot_no        smallint NOT NULL,          -- 1 | 2 | 3
    status         text NOT NULL,              -- Deploying | Active | Draining | Retired
    deployed_at    timestamptz NOT NULL,
    drained_at     timestamptz,
    drain_deadline timestamptz                 -- max time to keep Draining for idle circuits
);

-- Single pointer for "where new sessions/agents go"
-- (a one-row settings table, or a typed key in the existing catalog settings)
current_default_release : release_id
```

The existing per-account `subdomain → shard → connection-string` mapping and account `status` (including `UPGRADING` for breaking changes) are unchanged and orthogonal. Like the rest of the catalog, these control-plane rows are cached per router/node with **explicit invalidation** on a default flip or status change.

## 5. When and where the catalog is read

- **Router (YARP), every request** — reads `current_default_release` and the `release_id → slot_no` map (cached, short TTL + push-invalidate on flip) to (a) route cookieless/retired requests to the default slot and issue the cookie, and (b) map a live cookie's release to its slot, falling back to the default if the release is `Retired`/unknown.
- **Deploy orchestration, on state change** — writes `Deploying` on deploy, flips `current_default_release` and writes `Active`/`Draining` at cutover, writes `Retired` when a release has fully drained.
- **App/slot, continuously** — reports its **active-circuit count** and **in-flight-deployment count** (health/metrics) so the orchestration can tell when a `Draining` release is empty.

## 6. Topology and routing

The front and app tiers are separate machines, with a deliberate split of responsibility — and of *trust*: only the app nodes hold database credentials.

**Front tier — Caddy, no DB access.** Caddy does TLS termination, certificate renewal, WebSocket pass-through, and **node-level sticky sessions** that fan out to the app nodes. It is **version-agnostic**: it knows nothing about `kd_ver`, releases, or slots, and needs no catalog/DB access. Its config is static. Because this tier is internet-facing, keeping all DB credentials off it is a real attack-surface reduction.

```caddyfile
# Front (Caddy): edge only — TLS, WebSocket, node affinity. No DB, no version logic.
*.krakendeploy.com {
    reverse_proxy app-node-1:8080 app-node-2:8080 app-node-3:8080 {
        lb_policy cookie kd_node     # pin a circuit to one app node (best-effort)
        health_uri /healthz
    }
}
```

**App tier — each node runs YARP plus all three slots, with DB access.** Because YARP "only chooses slots," it lives **on the app node beside the slots**, so its routing is localhost. Every node runs all three slots, so any node can serve any release. The node's YARP:

- reads `current_default_release` and `release_id → slot` from the catalog (cached; the node has DB access anyway);
- routes a request carrying `kd_ver` to the matching **local** slot (fallback to the default slot if the release is `Retired`/unknown);
- on a cookieless or retired request, routes to the **local default slot** and issues `Set-Cookie: __Host-kd_ver=<current_default_release>`.

YARP fits the role: it's .NET and extensible, supports **dynamic in-memory configuration** (`IProxyConfigProvider`, swapped at runtime when the default flips), has **built-in cookie session affinity**, and **proxies WebSockets**.

**Why not let Caddy do the version routing?** Caddy *can* match the `kd_ver` cookie statically, but it can't route a *cookieless* request to the *current default* slot without reading the catalog — which would force a config reload per deploy, and a reload force-closes every live WebSocket fleet-wide. Putting all version logic in the per-node YARP avoids that, keeps the front DB-free, and makes the slot decision localhost. So: **Caddy is edge + node affinity; YARP owns releases.**

**Topology:**

```
   Caddy front tier  — HA: 2+ nodes behind a floating/reserved IP or L4 LB
   TLS + WebSocket + cert renewal + node sticky-session.   NO DB ACCESS.
            |                        |                        |
            v                        v                        v
   +-----------------+      +-----------------+      +-----------------+
   | App node 1      |      | App node 2      |      | App node 3      |
   |  YARP (local    |      |  YARP           |      |  YARP           |
   |   release->slot)|      |                 |      |                 |
   |  slot1 2 3      |      |  slot1 2 3      |      |  slot1 2 3      |
   |  DB ACCESS      |      |  DB ACCESS      |      |  DB ACCESS      |
   +-----------------+      +-----------------+      +-----------------+
```

**Do not run a single Caddy node.** Separating Caddy onto its own tier is good, but one front node is a fleet-wide single point of failure. Run two or more Caddy nodes behind a floating/reserved IP (keepalived/VRRP) or a cloud L4 load balancer. The no-DB benefit stands; the SPOF does not.

## 7. Two-level affinity (and why it's robust)

- **Level 1 — node:** Caddy's `kd_node` sticky-session cookie pins a circuit to one **app node** for reconnects (front tier).
- **Level 2 — slot:** that node's YARP routes by `__Host-kd_ver` to the right **local slot**, where the circuit's process state lives.

They compose, and the design is forgiving because `kd_ver` pins a **release, not a node**, and every node runs every slot. If node affinity misses — the sticky node is down, or Caddy falls back — the reconnect lands on a *different* node, but YARP there still routes `kd_ver` to the *same release* (that node's instance of the slot). The worst case is a fresh circuit on the same release (additive-safe; `[PersistentState]` restores annotated state), never a wrong-version circuit. So node affinity is a circuit-reattach optimization; release correctness never depends on it.

## 8. Deploy runbook (round-robin, additive)

```
0. Target slot = the Retired (idle, fully drained) slot. If none, wait or add a slot.
1. EXPAND migrations (additive only) applied to all shards. Verify no drift.
2. Deploy the new release -> target slot across all nodes. Mark release Deploying.
3. Smoke / health-gate the new slot (synthetic login + a sample job).
   Do NOT flip the default until it is green.
4. Flip current_default_release -> new release; mark it Active;
   mark the previous default Draining. Invalidate catalog cache on all routers.
5. New sessions / agents now get the new release (cookie). Existing ones stay
   pinned to their (now Draining) release until their work finishes.
6. Draining release receives no new work; its circuits and in-flight deployments
   finish naturally. When active circuits == 0 AND in-flight deployments == 0
   (or past drain_deadline for idle circuits), mark it Retired -> next target.
7. CONTRACT migrations in a LATER release, once the pre-contract release is Retired.
```

## 9. Drain and retire

- A `Draining` release is `Retired` only when its slot has **zero active circuits and zero in-flight deployments**.
- `drain_deadline` caps how long to keep it for idle circuits. Past it, stop pinning stragglers: their next request re-pins to the default, their circuit drops and reconnects to the default slot (additive-safe; `[PersistentState]`-annotated state is restored, the rest re-renders).
- **Never force-kill an in-flight deployment.** Let it finish; simply don't `Retire` the slot until it does. The drain deadline applies to idle *circuits*, not to running *deployments*.
- **Claim-coordination changes are NOT slot-overlap-safe.** A Draining slot keeps
  consuming its in-process task channel (pinned circuits may start deployments), so
  during overlap BOTH binaries run `ServerTaskLease.TryClaimAsync` against one
  Postgres. The claim's mutual exclusion holds only while both binaries contend on
  the SAME advisory-lock key and evaluate the SAME deferral predicates — a release
  that changes the lock key (as F6 did, per-key → constant `ClaimDecisionLockKey`)
  or the claim predicates lets an old-slot and a new-slot claimant pass each
  other's checks under READ COMMITTED and double-claim. Such a release must be
  rolled out under maintenance mode, or with the draining slot's dispatch fully
  stopped, not as an ordinary additive slot flip. (Moot for F6 itself:
  pre-production, no pre-F6 binary will ever be deployed.)

## 10. Composition with the schema strategies

- **Additive (this scheme):** slots + expand-before-flip + contract-in-a-later-release. No account ever sees downtime.
- **Breaking (not this scheme for schema):** use the per-shard `UPGRADING` flag + queue-quiesce + a **straddle release** (a build that tolerates both schemas). The straddle release rides the slots like any other release — deploy to a slot, flip the default to it — then run the per-shard DDLs under `UPGRADING`, then deploy the final (new-schema-only) release into the next slot. The slots carry breaking-change releases; their schema safety comes from straddle + quiesce, not from the slots.

## 11. Decisions

- **D-bg-1** Monolith versioned as a unit — a slot is one full app (UI + orchestrator co-deployed). No tier decoupling.
- **D-bg-2** Cookie keyed on opaque `release_id`, not raw slot number, to stay unambiguous during a slot's fleet rollout.
- **D-bg-3** The default-version decision lives in the router (YARP) reading the catalog, never in proxy config — so deploys never trigger a config reload, and live WebSockets are never force-closed by a deploy.
- **D-bg-4** Slot count is a tuning parameter; three is the floor (one drain overlap). Increase if deployments outlive the deploy cadence.
- **D-bg-5** ~~Single-node local installs need neither slots nor YARP (stop → migrate → start). Slots + YARP are the SaaS / multi-node-HA mechanism.~~ **SUPERSEDED by BG1/T1+T2 (2026-07-28):** blue-green is available on-prem too — `Deployment:Topology=OnPremBlueGreen`, single box is the supported minimum (§12). `Topology=OnPrem` (the default) keeps stop → migrate → start.
- **D-bg-6** Front (Caddy) and app tiers are **separate machines**, and **only app nodes hold DB credentials** — the internet-facing front holds none. The front tier must be **HA** (two or more Caddy nodes behind a floating/reserved IP or L4 LB); a single front node is a fleet-wide SPOF.
- **D-bg-7** Caddy is **version-agnostic** (edge + node affinity only). All release/slot routing lives in a **per-node YARP co-located with the three slots**, so the slot decision is localhost and the front needs no catalog/DB access.

## 12. On-prem blue-green (BG1)

Since BG1 (2026-08-27) the whole scheme runs on-prem: `Deployment:Topology=OnPremBlueGreen`
(chosen at install — kraken-init/`database setup --topology` prompt). Competitive context:
Octopus's documented HA upgrade is a full-cluster outage ("All Octopus Server nodes must run
the same version of Octopus Deploy"); no blue-green of the Octopus Server exists anywhere.
Ours flips a slot, and the draining release keeps its OWN orchestrator until its in-flight
deployments finish.

**What changes per topology (T2/T3/T5):**

| | `OnPrem` (default) | `OnPremBlueGreen` | `Saas` |
|---|---|---|---|
| Registry (`app_releases` + `platform_settings`) | not registered | KrakenDb, dedicated `platform` schema, own `__EFMigrationsHistory_platform` | catalog DB, `public` schema (catalog migrations own the DDL) |
| Registry context | — | `PlatformReleaseDbContext` (KrakenDeploy.Platform) | same context, catalog connection |
| Router | none — Caddy → server | per node: Caddy → router → slots; router's conn string carries `Search Path=platform` (raw reads stay unqualified — code untouched) | per D-bg-6/D-bg-7 |
| Hangfire schema | auto-migrated at boot | `PrepareSchemaIfNecessary=false` at slot boot; created/upgraded ONLY by `database setup`/`upgrade` | same as OnPremBlueGreen, storage in the catalog |
| Drain machinery | not registered | `DrainModeHangfireStopper` + `kraken.release-drain-watch` + the worker's drain claim gate | same |
| Front tier (T5) | Caddy → server | single box: Caddy → router → slots; multi-node: Caddy front (TLS + node distribution + health-drain) → per-node routers | Caddy HA front (D-bg-6) |

The Router is the entry point of a NODE, never of the installation; Caddy is the TLS front in
every on-prem install and the YARP router never terminates TLS (T5). Single box is the
supported minimum. Delivery: the `bluegreen` compose profile in `deploy/onprem` (BG1); bare-
metal Windows-service slots are BG2.

**The expand/contract contract (T4/T10).** Choosing `OnPremBlueGreen` commits the install to
ADDITIVE-ONLY migrations while more than one release is live:

- A non-additive EF migration carries the `[StopTheWorld]` attribute (Server.Core). The
  WP-BASELINE lint will enforce markers by operation analysis; until it lands, review is the
  guard — mark anything with Drop*/Rename*/narrowing Alter.
- `database upgrade`/`setup` refuse a MARKED pending migration while the registry shows
  another non-Retired release, naming the migration and the runbook; a purely-additive
  pending set proceeds — that IS the rolling upgrade. `--stop-the-world` overrides after the
  documented full-stop runbook (docs/on-prem-guide.md).
- The SHARED Hangfire storage schema is always treated as marked: its pending state is
  version-checked (installed `hangfire.schema.version` vs the highest embedded
  `Install.v{N}.sql` in the loaded Hangfire.PostgreSql assembly), and slot boots never
  auto-migrate it. CI's storage-package watch flags `Directory.Packages.props` bumps of
  storage-schema-owning packages so the stop-the-world need is visible at review time.

**Drain gates the claim loop (BG1 item 10 — closes grill B1).** `DrainModeHangfireStopper`
alone was not enough: a draining slot's DeploymentWorker kept CLAIMING (cookie-pinned users
create work there; the create-time enqueue wakes THAT process), so a busy instance never
retired and post-flip work executed on old code. The worker now pre-checks
`ISlotDrainGuard.IsOwnReleaseDrainingAsync` before every `TryClaimAsync`/`TryResumeAsync`
(`DrainBlocked`, logged like `MaintenanceBlocked`); refused tasks stay `Queued` and the
ACTIVE release's minutely re-signal picks them up (its Hangfire server is alive while the
draining slot's is stopped). Children of a parent already claimed on the draining slot are
exempt (`ServerTaskLease.IsContinuationOfClaimedParent`). Placement deliberately differs
from the maintenance gate: drain is per-process identity (registry via `SlotDrainGuard`,
15 s TTL acceptable — drain is not a correctness switch), maintenance is instance-wide DB
state (`ServerTaskLease`).

**Maintenance mode composes with this** (T11–T13, landed `e27c89a` + BG1 item 9): the
stop-the-world runbook turns maintenance ON first — creation refusal (service layer,
unconditional, `ParentTaskId`-exempt) + the claim gate stop the queue while in-flight work
completes; queued + scheduled work fires at the first re-signal after maintenance ends.

## Implementation notes

Implemented 2026-07-02; BG1 topology split 2026-08-27. Component map:

| Design element | Implementation |
|---|---|
| §4 release registry + default pointer | Tables `app_releases` + `platform_settings` (`current_default_release`), owned since BG1 by `PlatformReleaseDbContext` (project `KrakenDeploy.Platform`). Saas: catalog DB `public` schema (catalog migration `AddReleaseRegistry` remains the DDL of record — the model-only `TransferReleaseRegistryToPlatform` migration is deliberately empty). OnPremBlueGreen: KrakenDb `platform` schema, own history table (`InitialPlatform`). Entity is `AppRelease` (the tenant domain already has an unrelated `Release`); status stored as int, not text. A filtered unique index enforces at most one non-Retired release per slot at the DB. |
| §5 orchestration writes | `ReleaseRegistry` (KrakenDeploy.Platform) — register/flip/retire, each transition serialized fleet-wide by a Postgres advisory transaction lock, key `KDRELREG` unchanged (concurrent CLI/watcher transitions cannot strand a second Active release or point the default at a Retired one). Driven by the `releases register\|flip\|retire\|status` CLI verbs (blue-green topologies; refused under `Topology=OnPrem`). |
| §6 per-node router | `KrakenDeploy.Router` — YARP **direct forwarding** (`IHttpForwarder`), not a dynamic `IProxyConfigProvider`: no proxy config exists at all, so a flip can never trigger a config reload (strictly stronger than D-bg-3). Preserves the client `Host` (account resolution). Catalog snapshot cached with a short TTL; degrade-stale is non-blocking (try-acquire refresh + failure back-off), so a catalog outage never serializes ingress. |
| §3 cookie/header | As designed, plus: over plain HTTP (dev/smoke) the cookie degrades to `kd_ver` (browsers refuse `__Host-` without `Secure`); the explicit `X-KD-Release` header outranks a cookie; a **`Deploying` release is reachable via the header only** — a browser cookie can never land on a build that has not passed its health-gate. |
| §5 slot telemetry | `/slot-metrics` on each slot instance (`{release, activeCircuits, inFlightDeployments}` — `CircuitCounter` + `InFlightWorkGauge`, release id from `Release:Id` stamped per slot at deploy). **Internal-only**: the router refuses to forward it; the drain-watcher probes slot ports directly. |
| §8-6 / §9 drain + retire | Hangfire job `kraken.release-drain-watch` retires a Draining release only when all its configured slot instances report zero circuits + zero in-flight work (probe failure or a release-id mismatch defers — never guess). §8-6 ("no new work") is enforced for background work by `DrainModeHangfireStopper`: a Draining instance stops its Hangfire server, so the shared schedule keeps running on Active instances. Manual `releases retire` cannot verify emptiness and says so — the watcher is the verified path. |
| Agent pin | Registration captures the router's `X-KD-Release` response header into `AgentIdentity.ReleaseId`; the hub connection echoes it so a mid-drain reconnect lands back on the slot holding the agent's in-flight orchestration state. |
| Ops surfaces | `POST /kd-router/invalidate` (push cache invalidation on flip/retire) requires the `Router:OpsToken` shared secret and is disabled without one — the router sits behind a pass-everything edge. |

**Requirements learned from the end-to-end smoke** (`scripts/smoke-bluegreen.sh`, CI: blue-green step of the smoke job):

- **Co-located slot instances MUST share the node's `Server:DataPath`** (one volume): the file secret store (`catalog-secrets.json` — the catalog holds only the secret ref), the Data Protection key ring (cookies must validate across releases or every flip logs everyone out), and the package/artifact tree. The server image pre-creates `/var/lib/krakendeploy` chowned to the app user as the canonical mount point.
- Infra probe endpoints must be Space-agnostic (`/slot-metrics` joined `/healthz` in `SpaceRouting.AgnosticPrefixes`), or the Space-URL redirect middleware 302s them into `/s/{slug}/…`.

**Accepted nuance:** a session pinned to a Draining release can still *initiate* new deployments from its circuit (the UI POST rides the pinned connection). That is inherent to circuit pinning; `drain_deadline` bounds it, and in-flight work still always completes (§9).

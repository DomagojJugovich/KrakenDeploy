# KrakenDeploy — Master Plan & Work-Package Prompts (v1 finish line)

| | |
|---|---|
| **Version** | 2.0 |
| **Date** | 2026-07-28 |
| **Authors** | Domagoj Jugović, Claude (Fable 5; 10-agent verification workflow) |
| **Status** | Review |
| **Technologies** | .NET 10, Blazor Server, Radzen, EF Core 10, PostgreSQL, SignalR, gRPC, Hangfire |
| **Scope** | Whole repo @ current local `main` |

> **NOTE — origin is current** since 2026-07-27 (`646e1d9`); the old "~154 commits ahead" warning is retired. Keep pushing after every landed WP — the SaaS-path smokes only run on push to `main`, and an unpushed disk is still an unbacked product.

---

## 1. Purpose & lineage

This is THE single planning document for KrakenDeploy. It supersedes:

- `docs/finish-plan-2026-07-05.md` (v1.3) — WP1–WP15
- `docs/production-fix-prompts-2026-07-13.md` (v1.1) — A/B/C/D series

Both originals are now **Archived**. Prompts for completed WPs remain there for reference; **do not execute open-WP prompts from the archived files** — every open prompt was re-verified against code on 2026-07-18 (10-agent verification sweep) and found to contain stale claims. The corrected prompts live here (§6).

Also folded in: the **2026-07-16 execution-engine adversarial audit** (E-series bug substance + the D1 merge design) and `docs/execution-engine.md` (the engine reference — prompts cite it instead of re-explaining the engine).

**Merged preconditions** (treated as done throughout): the db-schema-hardening chain (`docs/db-schema-fix-prompts-2026-07-10.md`, fixes 1–7) and the A/B/C series. Caveats carried forward honestly:

- **A2/A3 completion is inferred from code** (`SecretRedactor` + `RequestLogRedaction` wired into the log/output path; `AgentHub` no longer assigns `Roles`), never verified by a dedicated WP record — confirm coverage if in doubt.
- **db-schema fixes 1–2 were not independently re-verified** on 2026-07-18 (fixes 3/4/6/7 outcomes — TPH spine, composite FKs, provenance, settings document — were incidentally confirmed). Low risk, noted for completeness.
- **E-series provenance:** E3, E5, E6–E9 were re-verified in code 2026-07-18; E1/E2/E4, the supervisor park and the blue-green stranding rest on the 2026-07-16 audit record (all confirmed unfixed at audit time; nothing since has touched them).

---

## 2. Completed work

Reference only — no prompts. All of this is on **local** `main` (unpushed; see the warning above).

| ID | Title | Findings closed | Evidence / date |
|---|---|---|---|
| WP1 | Deploy from the web UI (`DeployReleaseDialog` + 5 entry points, FailureMode selector, schedule, freeze pre-check) | top product gap | merged 2026-07-10 (merge `4712364`) |
| WP2 | Cancel / redeploy / real Regenerate | product gap | merged 2026-07-10 (`c3413e3`); cancel since upgraded by B5/B6 (xmin-guarded writes, agent process-tree kill) |
| A1 | Package-upload path sanitization | T0-5 | `f086210` |
| A2 | Secret masking in task logs + sensitivity plumbing | T0-6, T1-6 (out-vars) | **Done (inferred)** from code |
| A3 | Remove agent role self-assignment | T1-7 | **Done (inferred)** from code |
| A4 | Sub-Space RBAC on the execution surface (strict matcher + `EnsureScopedAsync`) | T1-8 | verified + completed 2026-07-16 |
| A5 | MCP read-tool authorization | T1-9 | `edbb707`, 2026-07-15 |
| A6 | SSRF hardening (deny-by-default RFC1918, pinning ConnectCallback) | T1-11 | done |
| A7 | Auth-session hardening (revocation, DP-ring cert, cookie, user disable/reset/delete) | T1-13, T1-14, M2 | done |
| A8 | Agent transport auth hardening (token versioning, iss/aud, DPAPI agent.json, offline fail-closed) | T1-12, T1-15 | done |
| B1 | Durable dispatch + reconciler + atomic claim (`ServerTaskLease`) | T0-1, T1-2 | done |
| B2 | Agent reconnect: unbounded + supervised + FIFO outbox + `DispatchId` idempotency | T0-2 | done |
| B3 | Disconnect reconciliation + always-armed wave deadline | T0-3 | done |
| B4 | Online cross-step output variables + server-side capture | T0-4, T1-6 | done |
| B5 | xmin optimistic concurrency + `ServerTaskStatusWriter` on all writers | T1-1, T1-5 | `22be409` |
| B6 | Agent abort + attempt-idempotency wire contract (v1) | T2-5, T1-3 | `e338442`/`0d2ce55` |
| B7 | `NodeTaskGate` node cap + safe package cache + retry re-resolve | T1-3, T1-4 | done |
| B8 | Real server↔agent transport round-trip test + PR smoke deployment | test gap | done; smoke found + fixed `58d84db`, `d82bbfa` |
| C2 | On-prem compose: DEK init + DataPath unify | T0-8, T0-9 | done |
| C3 | Production hardening (all-env DI validation, Npgsql retry/pool, `/health/ready`) | T1-18, T1-19, P1 | done |
| C5 | Windows/Croatian script correctness (`.ps1` UTF-8 BOM, Desktop-default on Windows, UTF-8 output both ends, BOM'd artifacts) | T1-20 | **done 2026-07-18** on `fix/ops-windows-script-encoding` |
| DP-CERT | On-prem DataProtection cert enablement (operator PFX mounted ro; fixes on-prem prod boot) | A7 enablement (T1-14) | `d1ee124`, 2026-07-16 |
| SCHEMA | db-schema hardening chain, fixes 1–7 (server_tasks spine, composite Space FKs, settings fold, provenance, channel rules) | chain | merged pre-2026-07-13; fixes 1–2 not re-verified (§1) |

**Landed outside any plan** (2026-07-10 … 2026-07-15; recorded here because no other doc tracks them):

| ID | What | Evidence |
|---|---|---|
| ENUM-WIRE | REST + MCP enums serialize as **names** (not integers); MCP optional filter params truly optional; CLI `--wait` treats `SucceededWithWarnings` as terminal success | `0cf2445`, `1f13bd8`, `4c5fa6c`, `9f3b94b` — contract-relevant, freezes at v1 |
| RET-FIX | Retention bug fixes: `SucceededWithWarnings` counted as terminal success in pruning; prune fires on orchestrated finalization + offline import; `keep<=0` = disabled; runbook-run keep-50 pruning shipped | `eb29095`, `4eb30ba`, `010eba1`, `6bb9860`, `f4caf56` |
| POLLER | Subscription-poller audit-loop fix (poller source = sink) | `c3413e3`, 2026-07-10 |
| TAGS | Extended tag sets rework (TagSets/TagSetDetail pages, `EntityTagEditor`, polymorphic `tag_applications`) | `c3413e3`, 2026-07-10 |
| TEST-INFRA | One shared Postgres container + per-class template-DB clones (~8 min → ~53 s); `McpIntegrationTests` fixed for A5 | `1339dfb`, `911a4a9` |

---

## 3. How to use

Same protocol as before: **one WP per session, on its own branch, `/code-review` before every merge.** Each §6 prompt is self-contained for a fresh Claude Code session on **Opus 4.8** (or the current best model). Paste, in order:

1. The **Common preamble** (below) — always.
2. The **Audit addendum** (below) — for prompts marked *(preamble + addendum)*.
3. The WP prompt.

Build + affected tests + `dotnet run` boot before finishing (preamble rule 9). Report honestly what was verified vs not (rule 10). Progress is tracked in the **Status column of §4** — nowhere else (decision 2026-07-18).

### Common preamble (paste before every WP prompt)

```text
You are working in D:\_GITHUB\KrakenDeploy — a self-hosted Octopus Deploy clone ("KrakenDeploy"):
.NET 10 (SDK 10.0.300 pinned in global.json), Blazor Server UI with Radzen components
(src/KrakenDeploy.Server), domain/services in src/KrakenDeploy.Server.Core and
src/KrakenDeploy.Server.Data (EF Core 10 + PostgreSQL), agent transport (SignalR + gRPC) in
src/KrakenDeploy.Server.Transport, Hangfire background jobs, CLI verbs in
src/KrakenDeploy.Server/Commands. Git repo; conventional commits; code/comments/commits in English.

House rules (each has bitten us before — do not skip):
1. Render mode is GLOBAL: App.razor applies InteractiveServer to all routed pages. Do NOT add
   @rendermode to pages. Never create a folder under Components/Pages named like a sibling
   .razor file (CS0101 collision).
2. Privileged Blazor handlers must re-check permission server-side via UiActionGuard
   (Guard.AllowAsync, bypassCache) — the RequirePermission UI gate is cosmetic. Match existing pages.
3. Never ConfigureAwait(false) in component lifecycle code. Circuit-scoped caches use
   ConcurrentDictionary.
4. New Space-owned entities implement ISpaceScoped (global query filter picks them up);
   agent-path writes stamp SpaceId from the parent. Composite-FK convention (2026-07-10
   schema chain): space-scoped parents carry UNIQUE (space_id, id); space-scoped children
   FK (space_id, parent_id) -> parent (space_id, id); join tables carry a stamped space_id.
5. Multi-account safety: new singletons/static caches must be account-keyed (mirror
   PerAccountOidcProviderCache) or registered Scoped-in-multi-account (see
   ServiceCollectionExtensions). NOTE: FeatureFlagService / MaintenanceModeService /
   PerformanceSettingsService were replaced by SettingsService in the 2026-07-10 schema
   chain — do not copy them as patterns. Background work items carry AccountId
   (TenantWorkItem pattern); workers wrap processing in WithAccount.
6. EF migrations: dotnet ef migrations add <Name> --project src/KrakenDeploy.Server.Data
   --startup-project src/KrakenDeploy.Server.Data --framework net10.0
7. src/KrakenDeploy.Execution stays Octostache-only (no Contracts reference); the Agent must
   never reference Server.*.
8. UI chrome: page-head/page-title pattern (see Pages/Lifecycles.razor); Radzen theme variables
   only — no hardcoded hex. State-changing services write audit entries (follow existing patterns).
9. Before finishing: dotnet build KrakenDeploy.sln (warnings are errors); run affected test
   projects under tests/; boot dotnet run --project src/KrakenDeploy.Server (Dev host validates
   DI scopes — captive dependencies fail here). Postgres via docker-compose. Empty dev DB →
   seed with the seed-demo CLI verb (src/KrakenDeploy.Server/Commands/SeedDemoCommands.cs).
10. Report honestly what you verified vs what you could not.
11. Settings (post 2026-07-10 schema chain): singleton/per-Space config lives in the unified
    settings table behind SettingsService (typed ISettingsDocument payloads; secrets as
    *Encrypted members — the DEK-rotation completeness test reflects over them). New knobs
    extend an existing settings document or add a new document+key — do NOT create new
    settings tables or columns. The settings DbSet is off-limits outside SettingsService
    (an architecture test enforces this). Engine runtime knobs (task cap, wave fan-out,
    wave/grace timers) belong in the Engine settings document added by F3 — once F3 has
    landed, extend that document rather than adding bare Engine:* config keys (config-file
    values remain fallback/seed).
12. The execution engine is documented in docs/execution-engine.md — read it before touching
    DeploymentWorker, AgentHub, or the agent transport.
```

### Audit addendum (paste after the preamble for prompts marked "+ addendum")

```text
AUDIT CONTEXT (2026-07-13 production-readiness audit — see docs/production-readiness-audit-2026-07-13.md;
2026-07-16 execution-engine audit for E-series):
- This task fixes a specific audited defect. The db-schema-hardening chain and the A/B/C series
  are MERGED (see master-plan-2026-07-18.md §2 for what is Done); line numbers in the audits have
  shifted — locate every anchor by SYMBOL name.
- Pre-production policy still holds: breaking changes to wire contracts / EF schema / REST / step
  names ARE allowed. Prefer the clean fix over a back-compat shim. No "soft-fallback for old data"
  branches — a violated invariant should throw, not paper over.
- When a fix changes a gRPC .proto, a SignalR hub interface, or an EF schema, say so explicitly in
  the PR description under a "CONTRACT CHANGE" heading — these freeze at v1 and reviewers must see them.
- Do not expand scope into other WPs. If you find an adjacent defect, note it in the PR, don't fix it.
- Sensitive data: never log secrets, connection strings, internal IPs, or AD structure. Sanitise
  examples. This product runs at RH state institutions under GDPR.
```

---

## 4. Dependency & sequence table

Statuses: ⬜ open · ✅ done · ⏸ parked. Sizes: XS < ½ day, S ≈ ½–1 day, M ≈ 1–3 days, L ≈ 3–6 days, XL ≈ 1–2 wks (Opus-assisted). This table is the **only** progress tracker (TASKS.md §M16 just points here).

| Phase | WP | Title | Status | Size | Depends on |
|---|---|---|---|---|---|
| 1 — engine correctness | E-A | Server orchestrator: hub false-terminal, cancel/ownership, gate deadlock | ✅ 9c3cc29 | M | — |
| 1 | E-B | Agent runtime: executor DI, supervisor park, gate wedge, outbox verdicts, output-var upsert | ✅ 76eab6b | M | — |
| 1 | E-C | Hub/transport hygiene: registry wipe, cancel re-push, retired-dispatch guard | ✅ 50c5bdd | M | — |
| 1 | E-D | Leftovers: staging paths, log-sequence counter, interim runbook reap (E9 — deleted by D1) | ✅ fix/exec-d-hygiene | M | — |
| 2 — ops (parallel OK) | C1 | Backup/restore image + round-trip CI (+ caddy README rider) | ⬜ | S | — |
| 2 | C6 | Agent self-upgrade atomicity + rollback (rewritten) | ✅ fix/ops-agent-upgrade-atomic | M | E-B |
| 3 — engine merge | D1 | server_tasks ENGINE merge (2026-07-16 design supersedes the old prompt) | ✅ P1 `0a8d1a5` · P2+P3 `e247c46` | XL | E-A, E-B, E-C, E-D |
| 3 | D3 | Promote control-flow config keys to typed columns (+ rolling-warning rider) | ✅ 3e2388a | M | — |
| 4 — engine features | F1 | Same (project, environment, tenant) deployment serialization | ✅ fa2fad5·de44a02·a4f3f85·a6384ee | M | D1 |
| 4 | F2 | Per-target "Allow parallel task execution" + execution-started deadline arming | ✅ merged `79f200e` + followups 1-10. One item DEFERRED by decision: the machine gate is per WAVE, not per plan (§F2-followups item 4) — documented as a Residual; scheduled 2026-07-25 as F5+F6. | M | E-B, D1 |
| 4 | F3 | Settings GUI: Engine document + AgentUpdate + logging + auth + SSRF | ⬜ | L | — |
| 4 | F4 | Remove the `ApiKey:Key` config auth path | ✅ fix/sec-remove-config-apikey | S | — |
| 4 | F5 | Machine gate reader-writer rework (mutual-consent shared mode + updater write-lock) | ⬜ | M | F2 |
| 4 | F6 | Server-side per-plan target exclusion at claim time (project/runbook consent flags, FIFO by overlap, reason string, /tasks?target filter) | ⬜ | M | F1, F5 |
| 4 | F7 | Shared-state hardening riders (parallel-safety audit 2026-07-25: secrets in %TEMP%, server script CWD, boot-sweep guard, ad-hoc dedup, hygiene) | ⬜ | M | — |
| 4 | BG1 | Topology split + on-prem blue-green (`Deployment:Topology` enum, platform release registry in KrakenDb, onprem `bluegreen` compose profile, non-additive stop-the-world guard) | ⬜ | L | D1; land before D4 and WP-BASELINE |
| 5 — product features | WP3 | Real manual intervention (pause/approve/reject) | ⬜ | XL | D1 |
| 5 | WP4 | Reachability + edit affordances (rescoped: 4 items) | ⬜ | S | — |
| 5 | WP5 | Missing CRUD end-to-end (target/release/group delete + user profile edit) | ✅ 5c23420·5d0d1b6 | M | — |
| 5 | WP6 | Finish project tabs (variables trio + runbooks tab) | ⬜ | L | — |
| 5 | WP9 | Retention expansion (rescoped: releases, packages, files, log age-cap) | ⬜ | L | D1 |
| 5 | WP8 | Prompted variables | ⬜ | L | WP1 (done) |
| 5 | WP7 | Triggers — all three kinds | ⬜ | XL | D1 |
| 5 | WP15 | Certificates library — full v1 | ⬜ | XL | — |
| 5 | WP10 | OpenTelemetry export (Server-only, rescoped) | ⬜ | M | — |
| 5 | WP11 | Latent bug batch (rescoped: 6 items) | ⬜ | M | E-D |
| 6 — structurals pre-freeze | D2 | Rename Deployment→Task wire/enum surface | ⬜ | L | D1 |
| 6 | D4 | Split Server.Data → Data + Application | ⬜ | L | — |
| 6 | D7 | Architecture-enforcement tests (delta only; D5-boundary asserts deferred) | ⬜ | S | D4 |
| 7 — final pre-freeze | WP-BASELINE | Migration-history squash + C4 data-correctness tests + expand/contract lint (**MANDATORY, not advisory — T4: BG topologies depend on additive-only migrations between live releases**) + v1 freeze checklist. The squash covers KrakenDbContext only; BG1's `PlatformReleaseDbContext` has its own history table and stays out of it. | ⬜ | M | every schema-touching WP above (D1, D3, F2, F6, BG1, WP3, WP5, WP7, WP8, WP9, WP15) |
| | | **GO-LIVE (~mid-Oct 2026; scope fixed, date flexes — drift from mid-Sept accepted 2026-07-18)** | | | |
| post | WP13 | Invites, signing-keys UI, AiCostOverride, authorized live log tail | ⬜ | L | — |
| post | D5-FOLD | "SaaS revival" package: D5 quarantine + D6 pooled factory + WP12 per-account DEK + blue-green stranding fixes + boundary tests. Part 1's blue-green decoupling ABSORBED by BG1 (pre-go-live, T7 2026-07-28). **WP12 acceptance: remove the multi-account smoke's `continue-on-error` AND add SaaS-mode blue-green coverage (flip/drain under multi-account — intentional per Domagoj 2026-07-27)** — the blue-green smoke itself goes green+blocking in BG1. | ⬜ | XL | D1, D4, BG1 |
| post | WP14 | Documentation reconciliation (expanded scope) | ⬜ | L | C1 (caddy rider) |
| post | WP16 | Script Console — hand-written ad-hoc runs (Tasks-page entry, signed, budgeted; placement movable pre-go-live if wanted) | ⬜ | L | F5, F7 |

Folded/retired rows (do not schedule separately): **C4** → inside WP-BASELINE; **WP12, D5, D6** → inside D5-FOLD; **E9** → interim, deleted by D1; **WP11's agent-update item** → inside C6.

---

## 5. Decision log

### Resolved 2026-07-06 (grill session with DJ) — carried verbatim from finish-plan §3

> Note: the D-numbers below are **decisions**, distinct from the D1–D7 **work packages** of the production-fix series. Prompts referencing them say "locked decision Dn (2026-07-06)".

| # | Item | Decision |
|---|---|---|
| D1 | Lifecycle auto-promotion | Skip — manual gates fit state-sector change control |
| D2 | Machine policies / health-check scripts | Keep on roadmap, post-go-live |
| D3 | Certificates library | **Pre-go-live, full scope** → new WP15 (store + IIS reference + expiry notifications + install-to-target step) |
| D4 | External feeds (Docker/NuGet) | Stays deferred |
| D5 | Non-AI script console | Keep on roadmap, post-go-live |
| D6 | EnvironmentDetail page + Access tabs + `AccessibleBy` filters | Keep on roadmap, post-go-live |
| D7 | Ephemeral environments (stub tab, no entity) | **Keep the stub**, design later — WP6 leaves it in place |
| D8 | Postgres tsvector search (locked decision, never built) | **Descoped** — remove from README and TASKS.md locked decisions |
| D9 | `Kraken` PowerShell module (locked decision, never built) | **Kept** as a future milestone — README marks it "planned", not shipped |
| D10 | Release version templates / auto-increment | Open (not decided) — small QoL, revisit post-go-live |
| D11 | `OctopusSystemVariablesBuilder` — 23 empty `TODO(kraken-equivalent)` variables | Keep on roadmap, post-go-live (fill the cheap ones first) |
| D12 | Guided failure mode | Skip — WP2 cancel + WP3 intervention cover the need |
| D13 | SaaS commercial layer (signup, billing, S3, Redis, status page) | Separate roadmap, not "finish the product" |
| D14 | MSI / .deb / .rpm installers | Deferred pending signing certs (as documented) |

Also locked in the same session: WP3 approval model (step-defined responsible teams; self-approval allowed; per-step auto-fail timeout, global default 72 h), WP2 boundary-cancel model, WP7 all three trigger kinds in one WP, WP10 verified against Seq in addition to neutral OTLP, `/code-review` gate on every merge, single serial Opus track, scope fixed / date flexes.

### Resolved 2026-07-18 (grill session)

| # | Item | Decision |
|---|---|---|
| N1 | Planning docs | One new master doc (this file); both originals archived with banners; open prompts re-issued corrected here |
| N2 | Completed WPs | Table only (§2), no prompts carried |
| N3 | Ordering | Foundations first: engine correctness (E) → ops → engine merge (D1) → engine features (F) → product features → structurals → baseline |
| N4 | D1 snapshot location | **Accessor resolution** — snapshots stay on `Release`/`RunbookRun`, kind-branched accessor; NO ServerTask column move (overrides the old D1 prompt's "hang ProcessSnapshotJson on ServerTask" suggestion) |
| N5 | WP12 (per-account DEK) | Folded into the post-go-live D5-FOLD package (multi-account stays deferred for v1). 2026-07-27 rider: WP12 also unblocks the blue-green smoke — blue-green is multi-account-only by D-bg-5, so its stack must set MultiAccount:Enabled and therefore hits the same DEK fail-fast; both smokes are `continue-on-error` until WP12 (commits `d140993`, `2c34c19`) |
| N6 | Blue-green stranding fixes (2026-07-16 audit) | Into the D5-FOLD package |
| N7 | F3 scope | All four groups incl. the AdministerSystem-gated SSRF allowlists |
| N8 | Go-live date | Drift from ~mid-Sept to **~mid-Oct 2026** accepted; scope stays fixed |
| N9 | E-series packaging | Four themed sessions (E-A orchestrator, E-B agent, E-C transport, E-D leftovers), not one-bug-one-branch |
| N10 | Migration squash | WP-BASELINE is the final pre-freeze WP and **absorbs C4** (un-deferred into it) |
| N11 | Tracking | This doc's §4 Status column only; no TASKS.md checkbox list is maintained |
| N12 | F1 key shape | Tenant is part of the serialization key; null tenant = its own key |
| N13 | F4 | Full removal of the `ApiKey:Key` config auth path (pre-v1 clean break) |
| N14 | D6 | Moved into the D5-FOLD package |
| N15 | D2 sequencing | **Stays gated on D1** — the digest's "unblocked" claim conflated the schema merge with the still-open ENGINE merge; renaming before D1 renames a surface D1 rewrites |
| N16 | C4 | ⏸ → folded into WP-BASELINE (see N10) |
| N17 | C5 | ✅ done 2026-07-18 |
| N18 | C6 | ✅ done 2026-07-19 (branch `fix/ops-agent-upgrade-atomic`). Whole-dir swap with backup + in-process rollback; SHA-256 verified on every apply (server computes it, never trusts the manifest field); refuses hashless / contract-skewed builds; post-restart health gate (registration-accepted) → commit-or-rollback; outcome reported to POST `/api/agents/update-status` as a Space-scoped target audit row. Absorbs WP11's agent-update item (multi-file payload). Residual: in-process updater cannot recover a hard-kill in the two-move window or a build whose apphost won't launch — marker persisted for a future external supervisor. |

---

### Resolved 2026-07-25 (grill session — per-plan target exclusion; F5/F6/F7)

Triggered by Domagoj's production observation on Octopus: per-script mutexes let two
deployment PROCESSES interleave at step boundaries and clash on shared folders —
"the only real protection is to let one plan finish, then the other." Verified from
Octopus Tentacle source (`ScriptIsolationMutex.cs`): their lock is an in-process
`AsyncReaderWriterLock`, acquired per SCRIPT, type-blind (no task-type on the wire),
and `NoIsolation` takes the READ side — the "bypass" is a downgrade to shared, not
a bypass. Octopus has no per-plan target exclusion anywhere; F6 is a deliberate
divergence. A 12-area parallel-safety audit of our own agent/server (2026-07-25) found
the engine internals safe by construction and produced F7's rider list.

| # | Item | Decision |
|---|---|---|
| P1 | Exclusion unit | Per TARGET, per WHOLE PLAN, enforced SERVER-side at claim time. State = in-flight `server_tasks` rows (no lock table — B1 lease/reconciler releases a crashed holder for free); one GLOBAL claim-decision advisory lock serializes claim decisions (subsumes/wraps F1's per-key lock). Blue-green safe: both slots share one Postgres |
| P2 | Consent model | Mutual-consent reader-writer: `mode(work on T) = Shared iff T.AllowParallel OR source.AllowParallel, else Exclusive`; co-run iff no writer present. One-sided consent still serializes — an author's confidence is one half of a two-party clash |
| P3 | Consent flags | NEW `Project.AllowParallelTaskExecution` (author consent, deployments) + SEPARATE `Runbook.AllowParallelTaskExecution` (Domagoj: separate flag, not one project knob). `Target.AllowParallelTaskExecution` keeps its meaning: operator forces Shared for the whole box. No scopeable/step-level consent — "very confusing for process author"; within-plan parallelism already exists (`StepStartTrigger.StartWithPrevious`) |
| P4 | Kind symmetry | Deployments AND runbook runs both participate (full symmetry). Runbooks stay F1-exempt for the (project, env, tenant) key. `Octopus.DeployRelease` children never conflict with their ancestor chain (E3 shape) |
| P5 | Ad-hoc | SHARED always, never excludes, never participates in the claim predicate. Wave-gap (ad-hoc slipping between two waves of an exclusive plan) explicitly ACCEPTED — no dispatch-time check. Semantics attach to the EXECUTION path (`AdhocScriptExecutor`), because a manual script console (Octopus targets-page analog) is planned and must inherit them |
| P6 | Ordering | FIFO by overlap — F1's discipline extended to set-valued keys: defer also when an OLDER due Queued task conflicts. Only CONFLICTING pairs order (mutual-Shared overlap neither defers nor orders). Starvation designed away; convoys accepted and made legible (P8) |
| P7 | Visibility | F1-style reason on the task ("Waiting for target X — busy with #N; M ahead") + ONE task-log line on first deferral. Tasks page gains `?target=` filter resolved via `task_target_assignments`; TargetDetail links "View tasks for this machine". No new page |
| P8 | Updater | Agent self-upgrade takes the WRITE lock for extract+swap+exit — fixes the audit CLASH (upgrade killed running ad-hoc work; check-to-swap TOCTOU) |
| P9 | Schedule | ALL of F5/F6/F7 land BEFORE go-live; breaking changes still allowed (pre-production). F6 joins WP-BASELINE's schema-dependency list |

### Resolved 2026-07-28 (grill session — topology & on-prem blue-green; BG1)

Trigger: Domagoj asked how Octopus handles on-prem HA upgrades and whether our blue-green
could serve on-prem too. Research (octopus.com/docs, primary sources cited in the BG1 prompt
and `reference_octopus_ha_upgrade` memory): Octopus's documented HA upgrade is a FULL-CLUSTER
outage — drain all nodes, stop all, upgrade the schema once, restart; "All Octopus Server
nodes must run the same version of Octopus Deploy"; mixed versions risk data corruption (their
own Kubernetes page); nothing blue-green exists for the Octopus Server product, on-prem or
cloud; best automated outcome they publish is a 5–10 minute outage. KrakenDeploy already has
the slot design — the draining release keeps its own orchestrator until in-flight deployments
finish — so offering it on-prem is a genuine differentiator, blocked only by the accidental
coupling of blue-green to `MultiAccount:Enabled`. This block supersedes **D-bg-5**.

| # | Item | Decision |
|---|---|---|
| T1 | On-prem blue-green | Blue-green (3 slots per node + per-node Router) is available ON-PREM, on one server or several. **D-bg-5 superseded** (it said slots+YARP are SaaS/multi-node-HA only; the code guard at `ReleaseCommands.cs:58` enforces the stale decision and is re-keyed by BG1). |
| T2 | Topology selection | New `Deployment:Topology` enum: `OnPrem` (default) / `OnPremBlueGreen` / `Saas`, chosen at INSTALLER level (kraken-init prompt). Ends `MultiAccount:Enabled`'s triple duty (tenancy / control-plane / blue-green): tenancy+control-plane consumers re-key to `Topology == Saas`, blue-green consumers to `Topology != OnPrem`. A config still carrying the old key fails boot with a named migration message (breaking allowed, pre-prod). |
| T3 | Registry home | The release registry stays TWO TABLES + the same `pg_advisory_xact_lock` transitions, extracted into a tiny `PlatformReleaseDbContext`: `Saas` → catalog connection (same tables it has today); `OnPremBlueGreen` → KrakenDb under a dedicated `platform` schema with its OWN migrations-history table (keeps WP-BASELINE's squash clean and expand/contract rules able to treat it as never-changing infrastructure). **Text file REJECTED**: transitions are check-then-act across two tables from separate PROCESSES (CLI invocations, drain-watcher on any node, router reads) — a file has no cross-process transaction; the failure mode is two Active releases or a Retired default = fleet-wide 503, and flock does not span nodes. **Main-DB-without-schema-separation rejected** for Domagoj's stated reason (confusing later) — the `platform` schema is the naming fix. |
| T4 | Non-additive migrations | BG topologies commit to expand/contract: ADDITIVE-ONLY while more than one release is live. A non-additive change ⇒ documented stop-the-world upgrade (drain all slots → stop → migrate → start) — i.e. exactly Octopus's only mode, as our worst case instead of our every case. Enforced: `database --upgrade`/`setup` REFUSES while the registry shows another non-Retired release (explicit `--stop-the-world` override for the runbook), and WP-BASELINE's expand/contract lint becomes MANDATORY. Hangfire storage-schema bumps count as non-additive (both releases share the single-DB Hangfire schema on-prem). |
| T5 | Front tier | D-bg-6 (separate HA Caddy front tier, 2+ nodes) is scoped to Saas. On-prem BG: the per-node Router IS the entry point; a single box running 3 slots + router is the supported minimum. |
| T6 | Delivery form (ASSUMPTION) | v1 on-prem BG ships as the DOCKER topology — `deploy/onprem` gains a `bluegreen` compose profile. Bare-metal Windows-service slot installs are deferred until a customer needs them. Awaiting Domagoj's confirmation; everything else in this block is decided. |
| T7 | D5-FOLD rescope | BG1 absorbs D5 Part 1's blue-green decoupling. D5-FOLD keeps: `IPlatformControlPlane` quarantine, D6 pooled factory, WP12 per-account DEK, blue-green stranding fixes, boundary tests. WP12 acceptance now: remove the multi-account smoke's `continue-on-error` + add SaaS-mode blue-green smoke coverage (blue-green under multi-account is intentional). |
| T8 | CI | BG1 re-points `docker-compose.smoke-bluegreen.yml` at `Topology=OnPremBlueGreen` (no multi-account, registry in KrakenDb) and REMOVES that step's `continue-on-error` — the blue-green smoke becomes green AND blocking pre-go-live. The multi-account smoke stays advisory-red until WP12. |
| T9 | Ordering refresh | Pre-go-live execution order: F5 → F6 → F7 → **BG1** → F3 → phase 5 (WP4 first — smallest — then WP3, WP6, WP9, WP8, WP7, WP15, WP10, WP11) → D2 → D4 (must account for the new Platform pieces) → D7 → WP-BASELINE → go-live. Scope fixed, date flexes (reaffirmed — BG1 is L-sized and accepted as pre-go-live scope). |

## 6. Work-package prompts

In phase order. Paste the Common preamble first; add the Audit addendum where marked.

### Phase 1 — engine correctness (E-series, four sessions)

#### E-A — Server orchestrator correctness *(preamble + addendum)*

```text
TASK: Fix three confirmed orchestrator bugs from the 2026-07-16 execution-engine audit (read
docs/execution-engine.md first — §2/§3 for the wave model, §6 for durability).

1. Hub false-terminal (E1). AgentHub.CompleteDeploymentAsync has a DB-fallback finalize for when
   subPlans.TryResolve finds no open slot. The fallback finalizes ANY task kind. Interleaving:
   server restarts mid-deployment faster than the lease expiry (5 min) — the worker's in-memory
   wave state is gone, the agent's buffered WAVE completion flushes from its outbox into the new
   process, TryResolve fails, and the fallback writes the WHOLE deployment Succeeded although
   remaining waves never ran (the boot reconciler defers to the still-live lease, and the
   !IsTerminal guard passes). Related: any single assigned agent can flip a farm-wide verdict this
   way, and eviction from the retired-dispatch LRU (16k) reopens the hole for long-lived tasks.
   FIX: restrict the fallback finalize to ServerTaskKind.RunbookRun (the hand-off model is the
   only legitimate user — see execution-engine.md §8) AND refuse it while ClaimedBy/lease is live.
   A deployment-kind completion with no open slot is logged and dropped, never finalized.
2. Cancel/ownership at batch boundaries (E2). Cancel is only observed at dequeue and wave
   boundaries; rolling BATCHES have no status check between them, so a zombie orchestration keeps
   dispatching batches after the reconciler or hub flipped the row. Also
   DeploymentWorker.IsCancellationRequestedAsync tests only == Cancelled (misses Failed flipped by
   the reconciler) and is typed against db.Deployments (D1 will generalize to db.ServerTasks —
   keep the new predicate compatible). FIX: one ownership predicate — "this task is still Running
   in the DB" via a fresh scalar projection — evaluated at wave, rolling-batch, and dispatch
   boundaries; stop cleanly when it fails. Additionally: a ServerTaskLeaseRenewal failure
   (lease lost) must signal a CancellationTokenSource that tears the orchestration down instead
   of letting it run leaseless.
3. NodeTaskGate deadlock (E3). DeploymentWorker acquires a NodeTaskGate slot
   (Engine:MaxConcurrentTasks, default 5) for the WHOLE orchestration;
   DeployReleaseStepRunner.WaitForChildAsync polls the child deployment in a while(true) with no
   internal deadline (bounded only by the optional per-step TimeoutSeconds — <=0 means unlimited
   in StepRetryRunner). The child needs a slot from the SAME gate: N parents waiting on children
   with N >= capacity is a permanent node-wide stall; recovery today is a restart. FIX: children
   (ParentTaskId != null) bypass the gate (they are accounted for by their parent's slot), AND
   WaitForChildAsync gets a default ceiling (config-backed, Engine section) independent of the
   per-step timeout. Keep the OCE-propagation contract: a ceiling hit classifies the step
   TimedOut, not generic Failed. Detect direct self-recursion (parent project == child project
   chain) at plan time and refuse.

Acceptance: an orchestrator-harness test proves a buffered deployment-wave completion arriving at
a fresh process does NOT finalize the deployment; cancel mid-rolling stops before the next batch;
a simulated lease loss cancels the orchestration; capacity-many parents with DeployRelease steps
plus their children complete without deadlock; WaitForChildAsync ceiling fires as TimedOut.
CONTRACT CHANGE: none (server-internal).
Branch: fix/exec-a-orchestrator
```

#### E-B — Agent runtime correctness *(preamble + addendum)*

```text
TASK: Fix five confirmed agent-side bugs from the 2026-07-16 execution-engine audit (agent
runtime + outbox; read docs/execution-engine.md §6 "Agent side" first).

1. DeploymentExecutor DI (E5). Agent Program.cs registers AddTransient<DeploymentExecutor>() while
   the class doc comment claims "App-lifetime singleton". AgentUpdateService ctor-injects its OWN
   transient instance, so its IsExecuting check reads a permanently-empty per-instance _running
   map — the guard is dead and the self-updater can swap binaries mid-deployment.
   FIX: AddSingleton (the class internally supports concurrent tasks via _running). Verify the
   other consumers: ServerLinkHostedService must resolve the same instance; OfflineRunner
   constructs its own (fine — offline is a separate process mode; confirm and note).
2. Supervisor park on reconnect-refusal. When OnReconnected registration is refused (e.g. B6
   contract gate), the handler calls StopAsync which sets _deliberateStop, so the Closed handler
   is suppressed and the supervisor stays parked on _closedSignal forever — a zombie agent that
   never retries. FIX: TrySetResult the closed signal in the refusal branch so the supervision
   loop observes the close and re-enters the retry policy.
3. Bounded gate-wait after supersede force-detach. After a supersede's unwind timeout
   (DeploymentExecutor.SupersedeUnwindTimeout, 30 s) force-detaches a non-cooperative step, that
   step still holds the machine _executionGate — the agent is wedged (heartbeating Online, never
   executing again). FIX: the new attempt's gate acquisition gets a bounded wait + escalation
   (log + report to server) instead of waiting forever behind a stuck step.
4. Outbox verdict exemption (E6). ServerLinkOutbox drops ANY item after
   MaxSendAttemptsPerItem (5) consecutive CONNECTED send failures — including StepCompleted and
   DeploymentCompleted, contradicting the doc comment that completions are never dropped (the
   never-dropped guarantee is only the capacity bound). Precision on realism: a transient
   duplicate-key rejection typically self-heals on retry (one failed send + one successful
   retry); the poison scenario needs a REPEATING hub-side fault — e.g. a ~30 s Postgres outage
   while the hub connection stays up returns 5 consecutive HubExceptions. Consequence: a dropped
   runbook-run completion now burns the 1 h wave deadline under a live lease then fails the run
   (post-D1 Phase 3; the MaxRunbookRunDuration reaper it used to hit is deleted); a dropped
   deployment wave completion burns the 1 h wave deadline then re-dispatches
   the whole sub-plan. FIX: exempt verdict-class items (StepCompleted, DeploymentCompleted, adhoc
   results) from the poison cap — retry forever with backoff (or park + re-flush on reconnect).
   Keep the existing drop behavior for log lines.
5. Output-variable upsert race. TaskOutputVariableStore.UpsertAsync is read-then-insert; the
   (TaskId, StepName, Name) unique index makes two concurrent callers (an at-least-once duplicate
   step report racing the original, or two parallel-wave targets sharing a stepName) both miss the
   read and both INSERT — DbUpdateException escapes AgentHub.ReportStepCompletedAsync. A retry
   typically self-heals (the reread finds the row and updates), but the throw is still a hub-side
   rejection that counts toward the outbox poison cap (item 4) and produces false
   ParallelOutputCollision audits. FIX: real upsert — PostgreSQL INSERT ... ON CONFLICT
   (task_id, step_name, name) DO UPDATE via raw SQL (encrypt sensitive values BEFORE binding,
   preserving the A2/T0-6 rules), or EF with a unique-violation catch + single reread-retry. The
   method is the single shared path for agent AND server-side capture (B4) — both get the fix.

Acceptance: updater refuses a swap while a deployment runs (test against the singleton);
reconnect-refusal leads to retry, not a parked supervisor; a wedged step cannot block the next
attempt forever; a completion item survives >5 consecutive connected send failures; concurrent
UpsertAsync calls for the same key do not throw. CONTRACT CHANGE: none.
Branch: fix/exec-b-agent
```

#### E-C — Hub/transport hygiene *(preamble + addendum)*

```text
TASK: Fix three confirmed transport-layer bugs from the 2026-07-16 execution-engine audit.

1. Connection-registry wipe (E4). InMemoryAgentConnectionRegistry.TryRemove unconditionally
   removes the _byTarget mapping. Interleaving: agent reconnects (OnConnectedAsync registers the
   NEW connection), then the OLD connection's OnDisconnectedAsync fires late (SignalR
   ClientTimeoutInterval, ~30 s, asymmetric drop) and wipes the LIVE mapping. The healthy agent
   becomes invisible: false Offline, its waves killed after the 2-minute disconnect grace, cancel
   pushes and token revocation silently no-op. FIX: compare-and-remove — remove the target
   mapping only when the registered connection id equals the disconnecting connection id; add a
   heartbeat-driven re-Add backstop so a wiped mapping self-heals within one heartbeat.
2. Cancel re-push on reconnect (E7). AgentCancelPusher.PushCancelAsync skips disconnected targets
   (no connection id -> continue), and NOTHING reconciles in-flight work on reconnect:
   AgentHub.OnConnectedAsync only registers the connection, RegisterAsync only does the contract
   gate + machine info, the agent's OnReconnected only re-sends registration. Consequence: an
   agent offline at cancel time reconnects and keeps executing the cancelled task TO COMPLETION —
   real side effects on the target machine; only the completion report is swallowed by the
   terminal-status guard. FIX: on (re)connect/registration, query this target's assigned tasks
   whose DB status is terminal (Cancelled/Failed) but which the agent may still be running, and
   push CancelDeploymentAsync for each. (Alternative — agent reports its in-flight task ids in
   AgentRegistrationRequest and the server answers with cancels — is a CONTRACT CHANGE via B6
   ContractVersion; prefer the server-side lookup, which needs no wire change.)
3. Retired-dispatch guard on the DB half (part of E-C hygiene). AgentHub.AppendLogAsync guards
   with subPlans.IsRetiredDispatch; AgentHub.ReportStepCompletedAsync does NOT — its in-memory
   half self-guards (RecordStepResult drops stale dispatchIds), but the DB half runs
   unconditionally: TaskOutputVariableStore.UpsertAsync overwrites the CURRENT attempt's output
   variables (the upsert key has no dispatch dimension) and TaskLogService.CompactStepAsync
   prematurely folds the current attempt's staged log lines mid-step. Trigger: a retired
   attempt's late step report flushed from the B2 outbox. FIX: mirror AppendLogAsync's
   IsRetiredDispatch guard into ReportStepCompletedAsync BEFORE the DB persistence half; keep
   RecordStepResult as-is.

Acceptance: an out-of-order disconnect(old) after connect(new) leaves the live connection
registered (test the registry directly); a target cancelled while offline receives a cancel push
on reconnect and its process tree dies; a replayed retired-attempt step report persists nothing
(register attempt B, retire attempt A, replay A's ReportStepCompletedAsync — assert A's outputs
absent and B's staged lines uncompacted). CONTRACT CHANGE: none (option 2 of item 2 explicitly
not taken).
Branch: fix/exec-c-transport
```

#### E-D — Leftovers: staging, log counter, interim runbook reap *(preamble + addendum)*

```text
TASK: Three residual engine-hygiene items from the 2026-07-16/18 verification sweeps.

1. Staging path lacks DispatchId + cleanup not in finally (E8). DeploymentExecutor builds step
   staging dirs as staging/{deploymentId:N}/{stepIndex} — plan.DispatchId is used everywhere else
   but not here, so a superseding re-dispatch shares the directory with the still-unwinding old
   attempt (SupersedeUnwindTimeout window) and can upload the OLD attempt's artifacts as the new
   attempt's. The per-step cleanup (Directory.Delete at the tail of ExecuteStepAsync) runs only on
   the normal exit path — skipped on the early failure returns (download/extract/ref-package) and
   on every OperationCanceledException (per-step timeout, cancel), so staging accumulates orphans
   on the COMMON paths. FIX: (a) include the dispatch id in the path —
   staging/{deploymentId:N}/{dispatchId:N}/{stepIndex}; (b) move per-step cleanup into a finally
   around the package/handler body; (c) add a deployment-level best-effort sweep of
   staging/{deploymentId:N} in ExecuteAsync's finally, plus an on-boot orphan sweep of the staging
   root. Cleanup stays non-fatal (catch-and-log).
2. Move next_log_sequence off the server_tasks row. TaskLogService.AppendLiveAsync allocates
   sequence numbers via a raw UPDATE on server_tasks.next_log_sequence — DB-atomic and correct,
   but it bumps the row's xmin, which is the B5 concurrency token. Frame: contention/efficiency,
   not correctness — chatty logs (multi-target waves; agent + orchestrator writers via
   LogSequencer) serialize on the task row's lock and churn xmin so ServerTaskStatusWriter burns
   retries under log load. FIX: move the counter to a one-row-per-task task_log_counters table so
   server_tasks xmin changes only on real state writes; batch bursts through the existing
   AllocateSequenceRangeAsync. MUST preserve the DB-atomic distinct-sequence guarantee that
   AgentHub, LogSequencer, and the offline import all rely on. Cross-link: WP11 item 3 adds the
   concurrent-allocation regression test — it targets THIS final shape (the counter table), so
   land this first and point the WP11 test at it.
3. Interim runbook disconnect-reap (E9 — INTERIM, deleted by D1). An agent that dies mid-runbook-
   run leaves the run Running for up to Engine:MaxRunbookRunDuration (default 1 h): runbook runs
   bypass the wave machinery (no sub-plan slot, lease released at hand-off), so the B3 disconnect
   monitor never engages; the only detection is the wall-clock ceiling in
   ScheduledDeploymentDispatchJob. Also: a runbook plan handed to a STALE connection id is a
   silent no-op — the run zombies for the full ceiling. FIX (deliberately small, marked for
   deletion): (a) a disconnect-aware reap — when the run's single assigned target has been
   continuously disconnected past Engine:AgentDisconnectWaveGrace, fail the run (target id from
   task_target_assignments; the job lives in Server.Data and the connection registry in
   Server.Transport — add a small abstraction seam, mirroring IAgentCancelPusher); (b) verify the
   connection is live at hand-off and fast-fail/retry instead of dispatching into the void. A late
   agent completion after the reap is already safely swallowed by the terminal guard. Mark both
   changes with a comment: superseded by the D1 engine merge (B3 then applies to runbook runs).

Acceptance: superseding re-dispatch cannot collide with the old attempt's staging files; staging
orphans are swept on cancel/timeout paths and at boot; log-heavy parallel deployments no longer
force status-writer retries (xmin stable across log appends — assert via test); a killed agent
fails its runbook run within the grace, not the 1 h ceiling; hand-off to a dead connection fails
fast. CONTRACT CHANGE: none (new task_log_counters table — migration).
Branch: fix/exec-d-hygiene
```

### Phase 2 — ops (parallel allowed)

#### C1 — Backup/restore image + round-trip CI *(preamble + addendum)*

```text
TASK: In-container backup AND restore are non-functional — the server image (repo-root
Dockerfile.server; deploy/ holds only compose/Caddyfile dirs) installs only curl, while THREE
consumers shell out to Postgres tools: CLI backup (BackupCommands, with a FindPgDump probe), the
Hangfire nightly BackupEngine (a DUPLICATED verbatim FindPgDump copy; its failures are captured,
not thrown — BackupService documents "never throws" — so containerized nightly backups fail
quietly with only an audit event), and CLI restore, the weakest: RestoreCommands launches bare
"psql" with NO discovery at all. The recommended on-prem deployment cannot back up or restore —
the DR mechanism is broken in the deployment we recommend.

Scope:
1. Install postgresql-client-16 (matching postgres:16-alpine in deploy/onprem/docker-compose.yml)
   in the runtime stage of Dockerfile.server, --no-install-recommends. Cover any other image that
   runs backup/restore.
2. Consolidate the duplicated FindPgDump (BackupCommands + BackupEngine) into one probe and give
   RestoreCommands the same discovery (its bare "psql" is the worst offender). The probe's
   hardcoded Windows paths cover PG 15/16 only — fine, but the apt install path inside the
   container must be found.
3. CI round-trip: the only workflow is .github/workflows/ci.yml (its "Restore" step is dotnet
   restore; none of the three smoke scripts touches backup). Add a job (or extend the smoke) that
   seeds a DB, runs the in-container backup CLI, restores into a fresh stack, and asserts login
   works AND a decrypted secret is readable (KEK provided via env — see C2). This is the
   acceptance gate for DR. Document loudly that ENCRYPTION_KEY must be preserved independently of
   the dump.
4. RIDER — deploy/caddy/README.md is DANGEROUS and ships with this fix: it claims the server
   auto-applies EF migrations on startup in Production ("no manual migration step required") —
   FALSE: Program.cs calls MigrateAsync only inside the IsDevelopment branch (locate by symbol),
   and deploy/caddy/docker-compose.yml runs Production with NO kraken-init service, so the guide
   leaves a production DB unmigrated. Its "manual fix" (docker compose exec ... dotnet ef database
   update) is also almost certainly broken — the runtime image carries no dotnet-ef; verify image
   contents before wording. Correct the README: add a kraken-init service like deploy/onprem, or
   document the CLI `database setup` invocation.

Acceptance: docker compose exec kraken-server ... backup produces a bundle; restore into a fresh
stack yields working login + a readable decrypted secret; the CI round-trip is green; the caddy
README no longer claims auto-migration and its upgrade steps actually migrate. CONTRACT CHANGE:
none.
Branch: fix/ops-backup-image
```

#### C6 — Agent self-upgrade atomicity + rollback *(preamble + addendum; depends on E-B)*

```text
TASK: Agent self-upgrade is a non-atomic swap with no rollback, no health gate, optional hash
verification, a dead in-flight guard, and — worst — it installs the WRONG THING. Verified state
of AgentUpdateService:
- Swap = File.Move(current->old) then File.Copy(newExe->current) then Environment.Exit(0). A copy
  failure (disk full, AV lock) between the two calls leaves NO exe at the current path — the
  service supervisor restarts into nothing.
- Only the single located KrakenDeploy.Agent(.exe) binary is copied out of the extracted staging
  dir. The agent is NOT PublishSingleFile, so the new apphost would load the OLD managed DLLs —
  the "upgrade" is a version-skewed no-op or bricks the agent. (This absorbs the old WP11 item 2;
  do not schedule that separately.)
- SHA-256 is verified only if the server sent a hash, and the check lives INSIDE the
  if(!File.Exists(downloadPath)) branch — a partial/corrupt download from a previous killed tick
  is re-used with NO re-download and NO verification. Server-side, the update-info endpoint
  returns the operator manifest's sha256 verbatim (defaults to "") and never uses
  ServerAgentUpdateService.ComputeSha256.
- The pre-swap in-flight guard reads DeploymentExecutor.IsExecuting — DEAD until E-B lands,
  because DeploymentExecutor is registered Transient and the updater's private instance has a
  permanently-empty _running map. THIS WP DEPENDS ON E-B (AddSingleton); after E-B the guard
  reads the real instance. Verify, don't re-fix.
- No upgrade-pending marker, no post-restart health check, no automatic .old restore; the only
  .old handling is deleting the previous one right before the next swap.

Scope:
1. Swap the WHOLE extracted publish directory (decision 2026-07-18; not exe-only, not
   PublishSingleFile): stage -> verify -> backup current dir -> move new into place -> on any
   failure restore the backup. Atomic-rename where the filesystem allows; never a state with no
   runnable agent.
2. Mandatory SHA-256: server always supplies a hash — wire ServerAgentUpdateService.ComputeSha256
   into the update-info endpoint instead of trusting the manifest's defaultable field. Agent
   refuses an update without a hash, and verifies on EVERY apply (move the check out of the
   download-only branch so cached/partial archives are verified too).
3. Health gate + rollback: write an "upgrade pending" marker before exit; on next start, if the
   new version fails to boot healthy within a timeout, roll back to the backup and report the
   failure to the server. Keep the backup until the new version is confirmed healthy.
4. Version-skew guard: refuse an update whose ContractVersion (B6) is incompatible with the
   server's advertised version; report the skew. Confirm the (post-E-B, now-live) in-flight guard
   blocks a swap mid-deployment.

Acceptance: simulated copy failure mid-swap -> agent still boots the old binary; a bad build that
fails the health gate -> automatic rollback + server-visible failure report; an update without a
server hash is refused; a corrupted cached archive is refused on apply; a multi-file payload
(appsettings, satellite DLLs) is fully installed; no swap happens while a deployment runs.
CONTRACT CHANGE: none (uses B6's ContractVersion).
Branch: fix/ops-agent-upgrade-atomic
```

### Phase 3 — engine merge + typed columns

#### D1 — server_tasks ENGINE merge *(preamble + addendum; supersedes the 2026-07-13 D1 prompt)*

```text
TASK: Finish the deployment/runbook unification at the EXECUTION layer. The data spine is unified
(server_tasks TPH); the engine is not — RunbookRunWorker is a degraded single-target
reimplementation. docs/execution-engine.md §8 carries the full capability matrix and the fork
inventory; read it before anything else. Latent runbook bugs this merge fixes: SECURITY — a
RunOnServer runbook step executes ON THE TARGET today (the partitioner never runs for runbook
runs); step failure reasons never reach the run log; the M14 step knobs (Condition, MaxRetries,
RetryDelaySeconds, TimeoutSeconds, Required) are dead for ONLINE runbook runs; no waves, no
rolling, no failure modes, no lease renewal, no orphan reconciliation, no scheduling.

Design LOCKED (2026-07-16 merge design + 2026-07-18 grill) — do not re-litigate:
1. Snapshot resolution = ACCESSOR. Process/variable snapshots STAY where they live
   (Release.ProcessSnapshot + Release.VariableSnapshot for deployments; RunbookRun.ProcessSnapshot
   + live variable resolve for runs). Introduce a kind-branched accessor abstraction the worker
   consumes. NO jsonb column move onto ServerTask.
2. Route runbook runs through DeploymentWorker, branching ONLY at the documented forks (the merge
   design's 16-fork inventory; the load-bearing ones: variable source, process-snapshot source,
   lifecycle gate, freeze gate, ScheduledFor, retention keep source, audit event names,
   cause/initiator vocabulary, drop-bundle support). Everything B1–B7 added (durable dispatch,
   disconnect reconciliation, wave deadline, cancel, idempotency, concurrency cap, status guards)
   applies to BOTH kinds through the single engine. Delete RunbookRunWorker + RunbookRunChannel
   (a thin Kind=RunbookRun entry point may remain).
3. Traps — each has a specific failure mode:
   - Retention: the worker's post-success prune must KIND-BRANCH keep sources (lifecycle-phase
     keep for deployments, runbook keep for runs) or runbook retention silently dies.
   - Audit vocabulary is ADDITIVE: new RunbookRun.* event names; NEVER rename existing
     Deployment.* events — SubscriptionMatcher wildcard filters break on rename.
   - Generalize the worker's db.Deployments reads to db.ServerTasks (IsCancellationRequestedAsync
     and friends) — otherwise runbook cancel goes unobserved by the merged orchestrator.
   - Reconciler transition (DONE, D1 Phase 3): arm 4b (MaxRunbookRunDuration ceiling) is DELETED —
     pre-production, so no in-flight legacy hand-off runs to drain; the lease-orphan arm now reaps
     LeaseUntil == null Running rows of BOTH kinds (every live orchestration holds a lease).
4. Capacity: post-merge, runbook runs acquire NodeTaskGate slots and hold the blue-green drain
   gauge — account for both in Engine:MaxConcurrentTasks sizing and drain semantics.
5. Locked decisions: the freeze gate is SKIPPED for kind=RunbookRun (Octopus parity — runbooks
   run during freeze windows); Octopus.DeployRelease is ALLOWED in runbook processes via the
   db.ServerTasks parent-load generalization.
6. Phases (separate commits/PR stages): Phase 1 — core merge (4–6 d). Phase 2 — multi-target
   trigger surface + ScheduledFor for runbook runs + shared detail tabs + runbook
   output-variable/step-outcome endpoints (2–4 d). Phase 3 — legacy deletion after one release of
   soak: reconciler arm 4b, E-D's interim runbook reap (marked for deletion), stale XML-doc
   claims (1–2 d).

Acceptance: a runbook run with a multi-target process, a run-condition, a step retry, a step
timeout, a RunOnServer step, and a scheduled start honours ALL of them (today it honours none);
the RunOnServer runbook step executes on the SERVER; cancel works for runbook runs; a run strands
neither on disconnect nor on restart (B1/B3 cover it); the degraded path is gone; orchestrator
tests cover runbook parity. CONTRACT CHANGE: runbook dispatch shape may change — note it.
Branch: refactor/eng-server-tasks-engine-merge
```

#### D3 — Promote control-flow config keys to typed columns *(preamble + addendum)*

```text
TASK: Flags the orchestrator BRANCHES on are still stringly-typed in the jsonb Config bag:
Octopus.Action.RunOnServer, Octopus.Action.MaxParallelism, Octopus.Action.ForEach.Collection,
Octopus.Action.ForEach.Parallel. Two are centralized as named constants (RunOnServer in
Contracts; MaxParallelism in RollingWindowResolver) so some call sites are typo-proof, but the
flattener, step form, Process.razor and the AI context builders still use raw string literals,
and there are NO typed ProcessStep columns for any of the four. The M14 knobs
(Condition/Required/retries/timeout/StartTrigger) were already promoted — finish the job while
the jsonb->column migration is still destructively cheap.

Scope:
1. Promote RunOnServer (bool), MaxParallelism (int?), ForEachCollection (string?), ForEachParallel
   (bool) to typed ProcessStep columns. Migrate seed/import data. The verified touchpoint list
   (larger than the old prompt's): DeploymentPlanFlattener, WavePartitioner,
   RollingWindowResolver (its MaxParallelismKey const becomes a column read), DeploymentWorker,
   ServerScriptStepRunner (synthesizes RunOnServer into plan-level Config — the WIRE
   DeploymentStepPlan.Config likely must KEEP the key for agent compatibility; decide and state),
   StepFormDialog.razor hydrate/persist blocks, Process.razor badge rendering, the Octopus
   importer/exporter, and the AI surface (BuiltInStepConfigCurators, ProcessContextBuilder).
2. Wire-contract subtlety: the agent-side plan still receives config as a string dictionary —
   promotion is a server/DB-side change; the flattener maps columns back into the wire Config.
3. Keep the Octopus-compatible Config keys ONLY at the import/export boundary; the engine
   branches on typed columns, never the raw dict. Extend ProcessValidator (leaf steps can't carry
   group-only flags; MaxParallelism must be a positive int — save-time validation falls out of
   the typed column).
4. RIDER — rolling visibility (2026-07-18): RollingWindowResolver deliberately falls back to
   no-cap on a malformed MaxParallelism and on mixed rolling ancestors — keep the fallback
   (1-at-a-time serialization would be worse) but make it VISIBLE: return a reason alongside the
   cap (None/Malformed/MixedAncestors/Resolved) and have DeploymentWorker emit a warning into the
   task log + an audit entry when a rolling group exists but batching was disabled. Add an
   informational nudge when the window >= alive target count (cap never fires). The typed int
   column kills the malformed case at save time going forward; the runtime warning covers
   imported/legacy data.

Acceptance: a mistyped flag can no longer silently change control flow; Octopus import/export of
these flags round-trips; flattener/ForEach tests green; a deployment whose rolling group was
disabled by malformed data shows a warning in its task log and audit. CONTRACT CHANGE: EF schema
(new columns) + step config shape — note it.
Branch: refactor/data-promote-controlflow-columns
```

### Phase 4 — engine features

#### F1 — Same (project, environment, tenant) deployment serialization *(preamble + addendum; depends on D1)*

```text
TASK: Enforce the Octopus-parity hard rule the engine currently lacks (execution-engine.md §7
records it explicitly): nothing prevents two deployments of the same project to the same
environment (+ same tenant) from running concurrently — even against the same targets. This is a
claim-time serialization, not a UI convenience.

Scope:
1. Extend ServerTaskLease.TryClaimAsync: the conditional UPDATE additionally requires
   NOT EXISTS (another kind=Deployment row with the same (ProjectId, EnvironmentId, TenantId) in
   Running), and the check+claim pair executes inside pg_advisory_xact_lock(hash64(project, env,
   tenant)) so two claimants cannot interleave between check and claim. NULL tenant is its OWN
   key (untenanted deployments serialize among themselves; different tenants proceed in
   parallel). Deployments only — kind=RunbookRun is exempt (post-D1 both kinds share this claim
   path, so express the exemption there).
2. A claim refused by the serialization predicate leaves the task Queued and must NOT consume a
   NodeTaskGate slot — order the serialization check before gate acquisition, or release the slot
   immediately on refusal. The existing minutely stale-Queued re-signal retries it; no new poller.
3. UI queue reason: Deployments/DeploymentDetail show "Waiting: another deployment of <project>
   to <environment> is running" for a blocked Queued task (derive from the predicate — a small
   read-side helper, no new state).
4. This rule is UNAVOIDABLE by design — no bypass setting, no per-project opt-out (decision
   2026-07-18). Document it in docs/execution-engine.md §7 (replace the "must first be built"
   paragraph).

Acceptance: two concurrent deployments of the same (project, env, tenant) — the second stays
Queued with the visible reason and starts only after the first goes terminal; different tenants
of the same project+env run in parallel; runbook runs are unaffected; a blocked task consumes no
gate slot (capacity-1 other deployments still run). Claim-race test via the orchestrator harness.
CONTRACT CHANGE: none (claim semantics + UI read model).
Branch: feat/eng-project-env-serialization
```

#### F2 — Per-target "Allow parallel task execution" *(preamble + addendum; depends on E-B, D1)*

```text
TASK: Octopus-parity per-target concurrency control. Today the agent serializes ALL work through
its machine-wide _executionGate, adhoc scripts bypass that gate entirely (audit note), and the
server arms the wave deadline at DISPATCH — so a sub-plan queued behind a busy target burns its
deadline while waiting. Sequence after E-B (the gate-wedge fix) and D1 (single dispatch path for
both kinds).

Scope:
1. New bool on Target: AllowParallelTaskExecution, default false. Target-edit UI checkbox with
   warning text (parallel steps on one machine can interleave file/IIS/service operations) and an
   explicit note that this NEVER bypasses the F1 project/env/tenant serialization — it only
   affects same-machine execution of DIFFERENT tasks.
2. Stamp the flag into DeploymentPlan at plan-build time. [CONTRACT CHANGE: DeploymentPlan gains
   a field — note it.]
3. Agent: when the flag is set, bypass the machine _executionGate for that plan; when unset, keep
   strict FIFO. Bring adhoc scripts UNDER the same gate (they currently bypass it), honoring the
   same flag.
4. New agent->server "execution started" notification: the agent reports when a dispatched
   sub-plan actually ACQUIRES the gate and starts executing; the server arms the wave deadline at
   that point instead of at dispatch. [CONTRACT CHANGE: new IAgentHubClient/hub message + B6
   ContractVersion bump — note it.] The deadline must still be bounded when the notification
   never arrives (dead agent): keep a dispatch-time ceiling as the backstop so B3's
   "always-armed" invariant survives.

Acceptance: two tasks to one default target serialize (FIFO); with the flag they interleave; an
adhoc script waits its turn behind a running deployment on a default target; a sub-plan queued
behind a long-running task does not burn its wave deadline while waiting (deadline arms at gate
acquisition); a dead agent still hits the backstop ceiling. CONTRACT CHANGE: DeploymentPlan
field + new hub notification + ContractVersion.
Branch: feat/eng-per-target-parallelism
```

##### §F2-followups — open findings from the max-effort review (2026-07-25)

**Status 2026-07-25: items 1-3 and 5-10 are DONE; item 4 is DEFERRED as a documented
design decision.** Kept below in full because the diagnoses are the rationale for the
fixes, and because item 4 stays open. Ordered by original severity.

1. ~~**The flag is inert.**~~ **DONE.** Both handlers are now detached
   (`return Task.CompletedTask`), the queued gate wait observes host shutdown, and
   handler faults are logged instead of left unobserved. Pinned by
   `ServerLinkHostedServiceTests.Deployment_push_handler_returns_without_awaiting_the_run`
   (fails by timeout against the old shape) plus three real-hub tests —
   `Second_deployment_to_the_same_machine_waits_for_the_gate`,
   `Parallel_flag_lets_a_second_deployment_run_while_the_first_blocks`,
   `Operator_cancel_push_reaches_a_running_deployment` — all three verified to FAIL
   against the pre-fix wiring and pass against the fix. B6's cooperative cancel now
   reaches a running deployment, which the same root cause had also broken.
   Original diagnosis: `ServerLinkHostedService` wired both push handlers as
   `Task.Run(() => …ExecuteAsync(plan), stoppingToken)`, which returns the UNWRAPPED
   work task; the SignalR client feeds all client invocations through one
   `SingleReader` channel and awaits each handler, so the agent processes exactly one
   push at a time. Measured on a real loopback hub (client 10.0.0): the second
   deployment starts only after the first ends, and a following ad-hoc push arrives
   ~3.1 s late. Consequences: `AllowParallelTaskExecution` yields no parallelism, the
   ad-hoc gate-queue/refuse path never runs, and B6's cancel push is queued behind the
   deployment it targets (~3.5 s late, after the run finished — so the process-tree
   kill never fires on an operator cancel). Concurrency returns only in a
   post-reconnect window (each `ReceiveLoop` allocates a fresh channel; ~5 s of real
   overlap measured after a TCP drop). Fix is `_ = Task.Run(…); return
   Task.CompletedTask;` — but that makes the gate, the B6 supersede path and
   cancel-while-queued live code for the first time, so it needs transport-level tests
   (`TransportRoundTripTests` is the right harness and today dispatches only one
   deployment per test).
2. ~~**The flag also bypasses the same-task supersede guard.**~~ **DONE.**
   `AcquireMachineSlotAsync` now checks `forceDetachedStuck` BEFORE the bypass and
   REFUSES (not serializes) a parallel-enabled re-dispatch whose predecessor was
   force-detached - a bypassing predecessor holds no slot to serialize the two attempts
   against, so refusing is the only honest answer. Pinned by
   `Parallel_flag_still_refuses_to_run_two_attempts_of_the_same_task`.
   Original diagnosis: the bypass returned before
   the `forceDetachedStuck` bounded acquisition, so two attempts of ONE task can run
   concurrently — colliding on `Stop/Start-WebAppPool`, the shared physical site path,
   the retention prune, and `Stop/Start-Service`. Contradicts the promise in
   `DeploymentTarget`, `DeploymentContracts` and the target-edit UI that the flag
   affects only DIFFERENT tasks.
3. ~~**A hung ad-hoc script holds the gate forever.**~~ **DONE.** ONE
   `Adhoc:MaxTotalDuration` budget (default 5 min, matching `AdhocDispatcher.DefaultTimeout`)
   now spans the gate wait AND the run, measured from receipt: expire while queued =>
   REFUSE; expire while running => kill the process tree and report the timeout. One
   budget rather than separate wait/run bounds because separate bounds still allowed
   late execution (3:59 queued + 5:00 running beats a 5 min dispatcher). Pinned by
   `Adhoc_script_that_outruns_its_budget_is_killed_and_releases_the_machine`.
   Original diagnosis: the ad-hoc invoker got `CancellationToken.None`, `ScriptRunner`
   has no internal timeout, and no ad-hoc abort exists on the wire; the deployment-side
   queue wait was unbounded, so every later deployment to that target failed at the
   backstop until the agent was restarted.
4. **The gate is per WAVE, not per plan** - **DEFERRED, documented, still open.** The
   server dispatches one sub-plan per wave, so the agent releases and re-takes the slot
   at every wave boundary and an ad-hoc script can still slot in against a half-applied
   box. NOT fixed here, deliberately: holding an agent-side gate across waves means
   holding it across a server round-trip with no dispatch guaranteed to follow (a
   cancelled or failed wave would strand the slot until process restart - strictly worse
   than the current gap), and the alternative is a server-side per-target lease, which
   is a different mechanism in a different process and a work package of its own, not a
   followup. `MachineExecutionGate` is deliberately NOT the place to fix it. Recorded as
   a Residual in `docs/node-concurrency-and-cache.md` and in `docs/execution-engine.md`'s
   history row. RESOLVED 2026-07-25 (grill session, §5): scheduled pre-go-live as
   **F5** (agent reader-writer gate) + **F6** (server-side per-plan target exclusion at
   claim time) — the server, which sees whole plans, is the only place per-plan
   exclusion is expressible; F1's claim-time machinery is the vehicle.
5. ~~**Unvalidated durations.**~~ **DONE.** `EngineOptionsValidator`
   (`IValidateOptions<EngineOptions>` + `ValidateOnStart`) refuses, by key name: a bare
   number (checked against the RAW config string - after binding, `"4"` and
   `"4.00:00:00"` are indistinguishable, and the realistic typo lands at four days,
   INSIDE any useful magnitude ceiling), a non-positive duration, anything above 7 days,
   and a non-positive `MaxConcurrentTasks`. Agent side, `Adhoc:MaxTotalDuration` warns
   and falls back on unparseable / non-positive / >24 h. Pinned by
   `EngineOptionsValidatorTests`, including a case proving the rejected ceiling really
   would have made `CancelAfter` throw. **F3 must keep this validation when these knobs
   move into the settings document.**
   Original diagnosis: `Engine:MaxTargetQueueWait` and `Adhoc:MaxQueueWait` accepted a
   bare number, which `TimeSpan` binding reads as DAYS; the former made `CancelAfter`
   throw above ~49.7 d and fail EVERY deployment at dispatch, the latter silently
   inverted the "never executes late" guarantee.
6. ~~**The re-arm is not clamped.**~~ **DONE.** `WaveDeadline.ComputeArmWindow` returns
   `min(executionBudget, remainingToBackstop)`, or `null` (cancel now) once the backstop
   is spent, so the backstop is a real ceiling and the worst case is no longer
   `2 x MaxTargetWaveDuration + MaxTargetQueueWait` (4 h on defaults). Every arm
   additionally passes through `ClampToTimerLimit`: an explicit `TimeoutSeconds` near
   `int.MaxValue` is ~68 years, which made `CancelAfter` throw
   `ArgumentOutOfRangeException` AT DISPATCH and fail the wave with a raw
   "Parameter 'delay'" before anything reached the agent (pre-existing since B3,
   reachable without any misconfiguration). Pinned by `WaveDeadlineClampTests`,
   including a property sweep asserting no input yields a delay `CancelAfter` rejects.
   The residual "a lost marker leaves the wave on the backstop" is by design - that is
   what the backstop is for.
7. ~~**`TryMarkExecutionStarted` burns the one-shot mark before invoking the callback.**~~
   **DONE.** The mark is still set first (concurrent redeliveries must not both arm) but
   is handed back if the callback throws, and the exception propagates rather than being
   swallowed - the one state the two-stage deadline must never be in is "running on the
   backstop while `ArmedForExecution` claims it started". Pinned by
   `A_throwing_callback_hands_the_one_shot_back_so_a_retry_can_still_arm`.
8. ~~**`ITargetConcurrencyPolicy` is a test seam in production code.**~~ **DONE.**
   Interface, `DbTargetConcurrencyPolicy`, the DI registration and the test fake are all
   deleted (~60 lines). `IAdhocDispatcher.DispatchAsync` takes
   `IReadOnlyDictionary<Guid, bool> allowParallelByTarget`; `AdhocSessionService`
   resolves it on the SAME DbContext that just approved the iteration, so the dispatch
   reads one consistent snapshot with no second scope, no second connection, and no
   captive-dependency hazard (the fresh scope also did not carry the circuit's account).
   Absent target => take the gate, matching the fail-safe convention
   `ComputeMaxRiskAsync` already uses. Pinned by
   `Dispatch_receives_the_per_target_parallel_flag_from_the_database` and
   `A_target_deleted_after_freezing_is_absent_from_the_flag_map` (real Postgres).
9. ~~**Docs to correct.**~~ **DONE.** `AgentHub.ReportExecutionStartedAsync`'s inverted
   claim is replaced by the three real guards (strict dispatch-id match, registry
   one-shot, clamped re-arm) and the honest direction (a matched report normally
   SHORTENS). `docs/adhoc-actions.md` v1.5: the unsigned `AllowParallelTaskExecution`
   field is now an explicit entry under "What the gate explicitly does NOT guarantee",
   and the stale `Adhoc:MaxQueueWait` row / diagram / history are corrected.
   `docs/disconnect-reconciliation.md` v1.2 documents two-stage arming +
   `Engine:MaxTargetQueueWait` + startup validation.
   `docs/node-concurrency-and-cache.md` v1.2 and `docs/execution-engine.md` v1.10
   likewise.
10. ~~**Test gaps.**~~ **DONE.** The two weak arming tests now queue LONGER than the
    execution budget and then do real work (new `FakeAgent.WorkAfterAcquiring`), which
    makes the arming POINT observable: both now FAIL under a bare-budget dispatch arm AND
    under true pre-F2 semantics (no re-arm), each verified by running that mutation. The
    tautological `harness.Gauge.Count.Should().Be(0)` is replaced by an elapsed-time
    bound on the wedged wave going terminal - the thing that actually frees the real
    gauge. `OutboxItem.ExecutionStarted` gains FIFO-order and advisory-poison-drop
    coverage in `ServerLinkOutboxTests`.
#### F3 — Settings GUI: Engine document + operational knobs *(preamble + addendum)*

```text
TASK: Give operators a settings GUI for the knobs that currently live only in config files. Per
house rule 11: new Engine settings DOCUMENT behind SettingsService + a Configuration page
section. File values remain FALLBACK/SEED; the UI shows the effective value AND its source
(file/DB/default). Each knob is marked "live" (applies immediately) or carries a restart badge.

Scope — four groups (all four are in scope; decision 2026-07-18):
1. Engine document (new ISettingsDocument):
   - MaxConcurrentTasks — restart badge. Settings default = 20 (decision 2026-07-18; this
     deliberately supersedes the compiled EngineOptions default of 5 — state the behavior change
     in the PR).
   - NEW: default target fan-out cap per wave — default 10, live. Caps parallel targets in a
     target wave when NO explicit rolling group sets MaxParallelism; an explicit rolling group
     overrides it (Octopus parity: default task/machine fan-out limits). Read at wave dispatch —
     independent of D3's column promotion (reads config, no dependency).
   - MaxTargetWaveDuration — live.
   - NEW (F2 breadcrumb, landed 2026-07-25): MaxTargetQueueWait — live. F2 added it to
     EngineOptions as a config-file knob (default 2 h); fold it into this document like the
     others. It is the QUEUE half of the wave deadline: the dispatch-time backstop is
     MaxTargetWaveDuration + MaxTargetQueueWait, and the wave's real budget arms when the agent
     reports gate acquisition. Validate > 0.
   - AgentDisconnectWaveGrace — live; validate > 30 s (must exceed the hub's offline marking) and
     < the wave ceiling.
2. AgentUpdate section: the update-feed knobs + an encrypted GitHub token card (*Encrypted member
   — the DEK-rotation completeness test picks it up).
3. Logging + auth: Serilog verbosity via a LoggingLevelSwitch (live); Auth
   SessionRevalidationMinutes and Server:BaseUrl (restart badges).
4. SSRF allowlists (A6's per-integration RFC1918 allowlists): AdministerSystem-gated, every
   change audited, and config-FILE values OVERRIDE the DB document for this group (operator pin —
   an operator who pins the allowlist in config cannot have it loosened from the UI). This
   inversion is deliberate and applies ONLY to the Ssrf group; state it in the UI help text.

Acceptance: each knob editable in the browser with permission gating + audit; live knobs apply
without restart (prove MaxTargetWaveDuration or verbosity); restart-badge knobs display the
badge; effective-value + source rendering correct for file-set, DB-set, and default; the SSRF
file-pin wins over DB; DEK-rotation completeness test covers the new encrypted member.
CONTRACT CHANGE: none (settings document + UI; EngineOptions default change called out in PR).
Branch: feat/settings-gui-engine
```

#### F4 — Remove the `ApiKey:Key` config auth path *(preamble + addendum)*

```text
TASK: Pre-v1 clean break (decision 2026-07-18): remove the legacy config-based API key
authentication path (ApiKey:Key) ENTIRELY. The DB-backed per-user API keys (M13.C.4 — X-Api-Key
against hashed per-user keys, with the policy-scheme fix) are the replacement and have been live
since 2026-07.

Scope:
1. Delete the config-key branch from the API-key authentication handler/policy scheme and every
   read of the ApiKey:Key configuration key (grep the full solution, incl. smoke scripts and
   compose files).
2. Startup guard: if ApiKey:Key is still configured, log a single loud warning naming the
   replacement ("create a per-user key via the apikeys CLI / Configuration > API keys") — do not
   fail the boot over a leftover config line.
3. Migration note in docs/on-prem-guide.md (one short paragraph) + a CHANGELOG entry (WP14 owns
   the file's creation; leave the entry in the PR description if CHANGELOG.md does not exist yet).
4. Update any smoke/CI script that authenticated with the config key to provision a real key via
   the apikeys CLI verb instead.

Acceptance: a request authenticating with the old config key is rejected 401; DB-backed keys
unaffected; smoke suites green using provisioned keys; boot warning fires when the stale config
is present. CONTRACT CHANGE: removal of a documented auth mechanism — breaking for any operator
using ApiKey:Key; note it loudly.
Branch: fix/sec-remove-config-apikey
```

#### F5 — Machine gate reader-writer rework *(preamble only; do after F2)*

```text
TASK: Rework the agent's MachineExecutionGate from a binary mutex into a reader-writer gate
(Octopus ScriptIsolationMutex parity — verified from Tentacle source: NoIsolation takes the READ
side of the same lock; "bypass" is a downgrade to shared, never an actual bypass), and put the
agent self-upgrade under it. Locked decisions P2/P5/P8 (§5, 2026-07-25).

Scope:
1. MachineExecutionGate: reader-writer semantics. EXCLUSIVE (write) is the default plan mode;
   SHARED (read) when the dispatched plan carries AllowParallelTaskExecution = true; ad-hoc
   acquires SHARED ALWAYS. Co-running requires no writer. Keep: idempotent Releaser, IsHeld
   observability (extend with reader count), bounded acquisition (TryAcquireNowAsync +
   AcquireAsync(timeout) in read AND write variants). Writers must not be starved by a stream
   of readers — pick/build a primitive that queues fairly.
2. DeploymentExecutor.AcquireMachineSlotAsync: flag=true → acquire READ instead of returning
   Bypassed-with-no-lock. The forceDetachedStuck refusal stays BEFORE acquisition and keeps
   refusing (two attempts of ONE task must not co-run even as two readers). Wedged-gate
   escalation keeps its bounded waits, now mode-aware.
3. AdhocScriptExecutor: acquire READ always; the single Adhoc:MaxTotalDuration budget from
   F2-followup 3 is unchanged (refuse if expired while queued, kill the tree if expired while
   running).
4. AgentUpdateService: take the WRITE lock for the extract+swap+Environment.Exit window (after
   the existing window/connected checks; keep IsExecuting only as a fast pre-check). Fixes the
   2026-07-25 audit CLASH: ad-hoc work was invisible to IsExecuting so the swap killed running
   scripts, and the check-to-swap gap was a TOCTOU.
5. CONTRACT CHANGE (WP16 OPEN-1 resolved 2026-07-27): RETAIN
   AdhocScriptCommand.AllowParallelTaskExecution. The agent maps it true → READ,
   false → WRITE. The AI-session dispatch path always sends TRUE (B2: gated AI scripts are
   read-always); the console (WP16) maps its per-run checkbox onto the field
   (unchecked → false → WRITE — the default). AgentContract.CurrentVersion 2 → 3 (an old
   agent reads true as full bypass — no lock at all — which is exactly the behaviour F5
   removes; skew must be refused). Update agent-wire-contract.md,
   adhoc-actions.md, node-concurrency-and-cache.md (two-mode gate + updater participation).
6. OfflineRunner unaffected (fresh gate per invocation, single plan).

Acceptance: read∥read co-run on one agent; write excludes reads and reads exclude a write (both
directions); ad-hoc∥ad-hoc co-run; ad-hoc queued behind an exclusive wave dies on its budget,
not on a gate timeout; the updater waits for ALL work kinds and blocks new work while swapping;
the supersede-refusal regression test still passes; contract-skew refusal updated for v3.
Mutation-verify the exclusion tests (invert a mode, tests must fail).
Branch: feat/eng-machine-gate-rw
```

#### F6 — Server-side per-plan target exclusion at claim time *(preamble + addendum; do after F5)*

```text
TASK: No two tasks operate on the same serial target concurrently, for the WHOLE plan duration —
claim-time exclusion in the server, closing §F2-followups item 4. Motivation (Domagoj,
production Octopus): per-script mutexes still interleave two deployment PROCESSES at step
boundaries; shared folders clash; the only real protection is one plan at a time. Octopus has no
equivalent (Tentacle source verified) — deliberate divergence. Locked decisions P1–P7 (§5,
2026-07-25).

Scope:
1. Schema/EF: projects.allow_parallel_task_execution + runbooks.allow_parallel_task_execution
   (bool NOT NULL default false) + migration. UI: checkboxes on Project settings and Runbook
   settings with the same warning-copy pattern as the target flag (author consent: "this
   process is self-contained — may run alongside other consenting work on a machine").
2. Plan build (DeploymentWorker): DeploymentPlan.AllowParallelTaskExecution =
   target.Allow || (Deployment → project.Allow; RunbookRun → runbook.Allow). Wire shape
   unchanged (bool).
3. Claim (ServerTaskLease.TryClaimAsync), applying to Deployment AND RunbookRun: under ONE
   global advisory lock (constant key — replaces or wraps F1's per-key lock; document which),
   run F1's (project,env,tenant) deferral (deployments only, unchanged) AND the target-conflict
   predicate:
   - per-target mode from the OR-composition above; conflict = a shared target where at least
     one side is Exclusive;
   - defer if any IN-FLIGHT task conflicts OR any OLDER already-due Queued task conflicts
     (FIFO by overlap; mutual-Shared overlap neither defers nor orders);
   - tasks with ParentTaskId never conflict with their ancestor chain (DeployRelease children);
   - target sets read from task_target_assignments (exist from creation for both kinds).
   Blocked tasks stay Queued; verify the minutely re-signal covers runbook runs too.
4. Reason surface: blocked claim records blocker (task id/title, target, queue position); task
   detail renders "Waiting for target X — busy with #N (title); M ahead"; ONE task-log line on
   FIRST deferral only. Extend F1's reason mechanism — do not invent a second one.
5. Tasks page: [SupplyParameterFromQuery] target filter on /s/{slug}/tasks, resolved via a
   task_target_assignments join in ServerTasksService (row Title never contains machine names —
   a string match is wrong); TargetDetail gets "View tasks for this machine" →
   /s/{slug}/tasks?target={id}. No new page.
6. Docs: execution-engine.md (claim chapter: two-layer model — server claim exclusion above the
   agent RW gate), node-concurrency-and-cache.md (wave-gap residual now applies to ad-hoc only),
   disconnect-reconciliation.md note: deferral happens BEFORE dispatch, so F2's queue-wait
   backstop arithmetic is untouched.

Acceptance (integration, real Postgres): exclusive-vs-exclusive same-target defers with FIFO
order; both-consenting co-claim; one-sided consent defers; runbook↔deployment symmetric both
directions; DeployRelease child claims while its parent is in-flight on the same target; disjoint
targets unaffected; two concurrent claimants with overlapping sets cannot both pass (advisory-lock
race test); the blocked task carries the reason and exactly one first-deferral log line;
/tasks?target= returns exactly the assignment-joined rows. CONTRACT CHANGE: none on the agent
wire; EF migration (two columns) — F6 joins WP-BASELINE's dependency list.
Branch: feat/eng-per-plan-target-exclusion
```

#### F7 — Shared-state hardening riders *(preamble only; independent of F5/F6)*

```text
TASK: Close the shared-state defects from the 2026-07-25 agent/server parallel-safety audit.
Re-verify each finding at HEAD before fixing (file:line refs are from 2026-07-25).

Scope, by severity:
1. Secrets in cleartext %TEMP% (GDPR-relevant — RH state institutions): the generated .ps1
   embeds ALL resolved variables INCLUDING sensitive values into the $OctopusParameters literal
   (ScriptStepHandler preamble; ServerScriptStepRunner equivalent) and writes it to
   Path.GetTempPath() — under LocalSystem that is C:\Windows\Temp with a permissive ACL;
   deletion is best-effort and skipped on abrupt exits. Fix: write scripts under the
   per-dispatch staging dir (agent) / a per-dispatch server work dir, delete in finally;
   evaluate passing sensitive values via environment instead of the script literal. Never log
   values while fixing.
2. ServerScriptStepRunner WorkingDirectory = Path.GetTempPath(): every server-side script on a
   node shares one CWD — relative-path I/O collides ACROSS deployments. Fix: per-dispatch work
   dir, cleaned in finally.
3. SweepOrphanedStagingOnBoot wipes the entire staging root with no ownership guard — a second
   agent process on one DataPath (misconfig / restart overlap) wipes the incumbent's live
   staging mid-step. Fix: lock-file/PID guard or sweep only provably-orphaned subtrees; log
   what was swept.
4. Ad-hoc frozen-target-set dedup (BLOCKS the planned manual script console): a duplicate
   targetId yields two identical concurrent commands sharing one workdir — the first finisher
   deletes the other's CWD mid-run. Fix: dedup at freeze (CreateSessionAsync) AND defensively
   in ParseFrozenTargets.
5. Hygiene batch: StepPackageLoader reads bare config["DataPath"] instead of Agent:DataPath
   (step-package cache lands in CWD-relative ./data; smoke compose carries a workaround — fix
   and remove it); StepPackageLoader._cache.TryAdd loser leaks a collectible ALC (unload on
   lose); AgentIdentityStore.SaveAsync is a non-atomic overwrite of agent.json (tmp+rename,
   like AgentUpgradeMarker); LocalPackageCache delta-base picks directory-enumeration order —
   pick highest version.

Acceptance: each of 1–5 lands with a test where a harness reaches it (dedup, atomic save,
DataPath key, delta-base order, per-dispatch CWD isolation) or a documented manual verification
(ACL check on the script location); all existing suites green; no new config keys. NOT in
scope: gate/claim concurrency semantics (F5/F6 own those).
Branch: fix/agent-shared-state-hardening
```

#### BG1 — Topology split + on-prem blue-green *(preamble + addendum; after F7, before D4/WP-BASELINE)*

```text
TASK: Make blue-green slot deployment available ON-PREM (1..N servers, 3 slots per node) and
introduce an explicit deployment topology, ending MultiAccount:Enabled's triple duty. Locked
decisions T1–T8 (§5, 2026-07-28); supersedes D-bg-5 in blue-green-slot-deployment.md.
Competitive context (sourced 2026-07-27): Octopus's documented HA upgrade is a full-cluster
outage; "All Octopus Server nodes must run the same version of Octopus Deploy"
(octopus.com/docs/best-practices/self-hosted-octopus/high-availability); their Kubernetes page
says mixed versions risk data corruption; no blue-green of the Octopus Server exists anywhere.
Ours: slot flip, and the draining release keeps its OWN orchestrator until its in-flight
deployments finish.

Scope:
1. `Deployment:Topology` = OnPrem (default) | OnPremBlueGreen | Saas. Re-key EVERY
   MultiAccount:Enabled read by its TRUE concern — grep-verified list 2026-07-28:
   - tenancy/control-plane → `Topology == Saas`: Program.cs boot (AddKrakenControlPlane,
     account-routed AddKrakenDeployData, M13.D.2 DEK fail-fast — stays for Saas until WP12,
     Hangfire-in-catalog at :807-810, ControlPlaneDevSeed at :894, :990 endpoints),
     OidcRegistrar.cs:89, HangfireJobRegistrar.cs:117 + AccountBackupRunner,
     ServiceCollectionExtensions.cs:96 (HttpAccountContext), AgentAccountHubFilter, CLI:
     CliHost.cs:73 (tenant resolution), EncryptionCommands.cs:318, BackupCommands.cs:45,
     RestoreCommands.cs:86, SeedDemoCommands.cs:33.
   - blue-green → `Topology != OnPrem`: ReleaseCommands.cs:58 (drop the D-bg-5 error text).
   A config still carrying MultiAccount:Enabled fails boot/CLI with a named migration message.
2. Extract `app_releases` + `platform_settings` into a minimal `PlatformReleaseDbContext` in a
   neutral project location (NOT ControlPlane — respect the coming D4 split; ReleaseRegistry +
   ReleaseDrainWatcher move with it; advisory lock key "KDRELREG" unchanged). Connections:
   Saas → catalog (same physical tables it reads today — no data move); OnPremBlueGreen →
   KrakenDb, dedicated `platform` schema, OWN __EFMigrationsHistory_platform (WP-BASELINE's
   squash of the app schema stays untouched). OnPrem: none of it is registered.
3. Router: code untouched — it already takes a plain NpgsqlDataSource + two raw SQL reads;
   point its connection at KrakenDb in on-prem BG. deploy/onprem gains a `bluegreen` compose
   profile: 3 slot services + router, shared DataPath volume (co-located slots MUST share it),
   operator DP-cert mount pattern from the C-series. Single box = supported minimum; the
   router is the entry point (T5; D-bg-6 stays SaaS-only).
4. Register DrainModeHangfireStopper + ReleaseDrainWatcher in the OnPremBlueGreen job path
   (absorbs D5 Part 1's decoupling item). Verify the draining release stops ITS Hangfire
   server; document that the shared single-DB Hangfire schema makes Hangfire storage upgrades
   NON-additive events (T4).
5. Non-additive guard (T4): `database --upgrade` and `database setup` refuse while the
   registry shows another non-Retired release; `--stop-the-world` override. on-prem-guide
   gains TWO upgrade runbooks: rolling additive (register slot → health → flip → drain →
   retire) and stop-the-world non-additive (drain all → stop → migrate → start).
6. kraken-init: topology prompt (default OnPrem) with one-paragraph consequence text for
   OnPremBlueGreen ("commits you to additive-only migrations between live releases;
   non-additive upgrades need a stop window").
7. CI (T8): rewrite docker-compose.smoke-bluegreen.yml → Topology=OnPremBlueGreen (drop
   MultiAccount__Enabled + the Catalog connection; registry + router on KrakenDb); REMOVE the
   blue-green step's continue-on-error — it must PASS and gate again. Multi-account smoke
   untouched (advisory-red until WP12).
8. Docs: blue-green-slot-deployment.md vNext (supersede D-bg-5, on-prem chapter,
   expand/contract contract), on-prem-guide.md BG chapter, saas-multi-account-architecture.md
   D7 note, execution-engine.md pointer.

Acceptance: the onprem bluegreen profile boots 2 live slots + router with NO multi-account
config; `releases register|flip|retire|status` round-trips against the KrakenDb registry; a
cookie-pinned session survives a flip; a drain lets an in-flight deployment finish on the old
release while new sessions land on the new one; `database --upgrade` refuses with a second
live release and proceeds with --stop-the-world; OnPrem default topology behaves exactly as
today (regression suites green); Saas topology unchanged (DEK fail-fast intact); stale
MultiAccount:Enabled config → named error; blue-green smoke GREEN and BLOCKING in CI.
BREAKING: config key replaced; new EF context + `platform` schema.
Branch: feat/topology-onprem-bluegreen
```

### Phase 5 — product features (corrected from the 2026-07-18 verification digest)

#### WP3 — Real manual intervention (pause / approve / reject) *(preamble only; depends on D1)*

```text
TASK: Replace the auto-approving manual-intervention step with a real pause/approve/reject flow.
ManualInterventionStepHandler logs "Step auto-approved (unattended deployment mode)" and returns
true; the step-schema UI text even tells operators Kraken auto-approves. There is no paused
status (DeploymentStatus is exactly Queued/Running/Succeeded/Failed/Cancelled/
PendingOfflineResult/SucceededWithWarnings), no Interruption entity/service/table/endpoint/dialog
anywhere, and DeploymentWorker's only gates are the NodeTaskGate, the freeze gate, and
Required-failure gates (a code comment explicitly calls an approval gate "a future approval
gate"). Target market is state-sector change control — this is the top parity gap.

Permissions — the REAL names (verified 2026-07-18): InterruptionView = 1110 and
InterruptionViewSubmitResponsible = 1111 ("Approve / reject a manual-intervention step") BOTH
already exist in the Permission enum and are already seeded into built-in roles. Wire
ENFORCEMENT; do not add enum members; any other permission name you find in older planning docs
is fictional.

Step 1 — design doc first: docs/design-manual-intervention.md (header: version/date/author/status
Draft, ~2 pages), then implement.
- DECIDED (2026-07-06, do not re-litigate): approvers = step-defined responsible team(s),
  Octopus-style — step editor gets a team multiselect; empty list = anyone in the Space holding
  the respond permission. Self-approval IS allowed. Per-step optional auto-fail timeout, global
  default 72 h — expiry fails the deployment exactly like a rejection (cleanup steps honored)
  with an audit entry noting timeout.
- Intervention is deployment-global: pause before the step's wave dispatches, not per-target.
- Offline drop bundles keep log+auto-approve, with an explicit warning line in the bundle log
  (the offline path already routes Octopus.Manual as a server-side step).

Step 2 — implementation, on the unified post-D1 spine:
- Interruption aggregate (ISpaceScoped, composite-FK per house rule 4): TaskId, StepId,
  instructions, responsible team(s), status Pending/Approved/Rejected, acted-by, notes, UTC
  timestamps.
- Pause semantics MUST integrate with the B-series machinery: a new non-terminal paused status
  (or flag) written via ServerTaskStatusWriter (B5 xmin); the worker persists state and FREES its
  NodeTaskGate slot (no thread parked); a paused task has no live lease — EXPLICITLY EXEMPT
  paused tasks from the B1 lease reconciler and the B3 disconnect monitor / wave-deadline reap,
  or they will be reaped as orphans. Resume re-enqueues through the existing dispatch path
  (TenantWorkItem, carrying AccountId — house rule 5).
- Record the approval outcome by extending StepOutcomeKind ADDITIVELY (its doc comment already
  anticipates e.g. ManualInterventionApproved).
- UI: banner + Approve/Reject dialog (notes mandatory on reject) on DeploymentDetail.razor,
  guarded by UiActionGuard Guard.AllowAsync(InterruptionViewSubmitResponsible); pending indicator
  on Deployments and Tasks pages gated on InterruptionView.
- Events: audit entries + a subscription-visible event type so M13.B subscriptions can notify
  approvers.
- Rejection → deployment fails cleanly; Failure/Always cleanup steps run per FailureMode.
- Runbook coverage: post-D1 both kinds share the orchestrator — decide in the design doc whether
  Octopus.Manual is allowed in runbook processes (Octopus allows it; recommend yes, same
  mechanics) and state the decision.
- Tests: pause frees the slot and survives reconciler+monitor passes; approve resumes; reject and
  timeout fail cleanly with cleanup; permission gating; harness coverage.

Acceptance: a process with a Manual Intervention step pauses with instructions visible; an
authorized user approves (continues) or rejects with notes (fails cleanly); a paused deployment
survives a reconciler pass and a server restart without being reaped; everything audited and
notifiable; deployments without the step unaffected.
Branch: feat/manual-intervention
```

#### WP4 — Reachability + missing edit affordances *(preamble only; rescoped to 4 items)*

```text
TASK: Wire existing, tested backend surface into the UI. Rescoped 2026-07-18: the original items
(c) tag-set/tag rename + tag-to-target assignment and (f) FailureMode selector are SHIPPED
(TagSetDetail + EntityTagEditor; DeployReleaseDialog) — do not rebuild them. Four items remain,
all verified still-missing. All handlers: UiActionGuard re-check; audit entries where the service
doesn't already write them; page-head chrome pattern.

1. NavMenu: add a "Step Packages" RadzenPanelMenuItem to the Library group in
   Components/Layout/NavMenu.razor pointing at Nav.Sp("/step-packages"). StepPackages.razor +
   StepPackageUsagePage.razor exist, are routed, and work — they are reachable only by typing the
   URL (the only inbound links are self-referential).
2. Tenant edit: TenantService.UpdateAsync + PUT /api/tenants/{id} exist with zero UI callers.
   Add an edit dialog (or inline) on Tenants.razor / TenantDetail.razor. NOTE the A4-era
   signature: UpdateAsync(id, name, slug, description, caller) now REQUIRES CallerAuthorization —
   pass Guard.CurrentCallerAsync() exactly as Tenants.razor's delete handler already does. Gate
   on Permission.TenantEdit (702).
3. Step-template edit: StepTemplates.razor has create/import/export/delete but no edit, and there
   is no detail page. Backend StepTemplateService.UpdateAsync(id, name, description, properties,
   parameters) + PUT /api/step-templates/{id} exist. Add an edit dialog reachable from the grid
   (decide dialog vs new detail page; dialog is cheaper). Gate on Permission.StepTemplateEdit
   (902). IMPORTANT (2026-07-18 review finding): unlike Tenant/Runbook, UpdateAsync takes NO
   CallerAuthorization — ADD the A4 pattern (CallerAuthorization parameter +
   EnsureScopedAsync-style scope check before the write) so step-template edit doesn't ship as
   the one unscoped mutation on the surface.
4. Runbook rename/description: RunbookService.UpdateAsync (A4-covered: CallerAuthorization +
   EnsureRunbookScopeAsync) + PUT /api/runbooks/{id} exist, zero UI callers. Wire an edit
   affordance on RunbookDetail.razor, threading the caller. Gate on Permission.RunbookEdit (801).

All four permissions already exist in the enum — no security plumbing beyond item 3's service fix.

Acceptance: each operation performable in the browser; step-template edit rejects a cross-scope
caller (test); no regressions on touched pages; build + affected tests green; manual smoke of
each page.
Branch: feat/ui-reachability-batch
```

#### WP5 — Missing CRUD end-to-end *(preamble only; rescoped)*

```text
TASK: Add destructive/administrative operations missing on BOTH sides. Rescoped 2026-07-18: user
disable/enable, password reset, and delete are DONE (A7 landed backend + UI) — do not re-prompt
them. All new mutations follow the A4 pattern: CallerAuthorization parameter,
EnsureScopedAsync-style check before the read, caller from Guard.CurrentCallerAsync(); confirm
dialogs state consequences; audit entries; tests. Prefer refusal over cascade surprises.

1. Target delete/decommission — greenfield end-to-end (no service method, no endpoint, no UI;
   only SeedDemoCommands has teardown logic). TargetService.DeleteAsync + DELETE /api/targets/{id}
   + UI on Targets.razor/TargetDetail.razor, gated on existing Permission.MachineDelete (413).
   Post-schema-chain FK reality: execution history references targets via
   task_target_assignments and step outcomes, both RESTRICT — retire/soft-delete is the ONLY path
   for targets with history (hidden from matching + dispatch, history preserved); hard delete
   only for history-free targets. Decide semantics for target_tenants and tag_applications rows,
   and whether a connected agent blocks deletion — a retired target's agent must be rejected at
   AgentHub connect.
2. Release delete — greenfield end-to-end. ReleaseService.DeleteAsync + DELETE endpoint + row
   actions on the Releases pages, gated on existing Permission.ReleaseDelete (303).
   server_tasks -> releases is RESTRICT: block deletion while tasks reference the release (or an
   explicit force that explains the interplay). Snapshot rows go with it. Coordinate with WP9's
   release retention so manual delete and retention share ONE code path (note: the retention
   pruning path is healthy — the 2026-07-14 fixes landed; coordinate with it, don't fix it). The
   blue-green *server release registry* under src/**/Releases/ is a DIFFERENT subsystem — do not
   touch it.
3. ProjectGroup rename + delete — greenfield. UpdateGroupAsync/DeleteGroupAsync on ProjectService
   (today only GetProjectGroupsAsync/CreateGroupAsync), extend ProjectGroupFormDialog to edit
   mode, group action buttons on Projects.razor, gated on existing ProjectGroupEdit (102) /
   ProjectGroupDelete (103). Project.ProjectGroupId is now REQUIRED (RequireProjectGroup
   migration; a default group is resolved at create) — delete must refuse non-empty groups OR
   reassign members to the default group; pick one and state it.
4. User profile edit — the only remaining user-management gap: an edit dialog (display name,
   email) on Configuration/Users.razor + a UserService update method mirroring SetDisabledAsync's
   UserManager-based pattern (normalize email), gated on existing Permission.UserEdit. There are
   no /api/users REST endpoints at all — add PUT /api/users/{id} alongside if cheap, else note.

Acceptance: each operation works from the browser with correct guards; deleting/retiring never
breaks existing history pages; retired targets can't reconnect; build + tests green (service-level
tests per item).
Branch: feat/crud-completion-batch
```

#### WP6 — Finish the project tabs *(preamble only)*

```text
TASK: Replace four "— pending" stub tabs under ProjectShell with real pages, and remove one
permanent placeholder. Verified 2026-07-18: ProjectShell's nav is fully built (10 primary tabs +
sub-tab families); exactly 7 stub pages exist. THIS WP covers 4 of them: AllVariables,
VariablePreview, TenantVariables, project-scoped Runbooks. Explicitly EXCLUDED: Triggers.razor +
RunbookTriggers.razor (WP7 — the trigger entity doesn't exist) and EphemeralEnvironments.razor
(locked decision D7 2026-07-06 — keep the stub). The variable engine exists — this is mostly
read-model UI over VariableService.

1. ProjectPages/AllVariables.razor: aggregated read-only view — project variables + linked
   library sets + tenant overlays, with scope columns (env/target/role/channel/tenant) and a
   source link. Reuse VariableService.ResolveAsync for precedence — do NOT reimplement it.
2. ProjectPages/VariablePreview.razor: pick environment (+ tenant/target/channel as applicable) →
   show the RESOLVED set exactly as a deployment would see it (sensitive masked), including which
   definition won and why. Reuse ResolveAsync.
3. ProjectPages/TenantVariables.razor: per-tenant variable values for this project. First check
   how tenant values are modelled (the resolver overlays tenant values; TenantCommon is
   "reserved — no creation path"). Scope: project↔tenant values only; do NOT invent a
   TenantCommon authoring flow — build the editor on whatever entity the resolver actually reads,
   and say so.
4. ProjectPages/Runbooks.razor: real project-scoped runbook list — pure filter-UI over the
   existing RunbookService/global Runbooks.razor, with inline create matching the global page.
5. Remove the permanently-disabled "Script Modules" card in ProjectPages/Process.razor (card ~
   lines 345-357, disabled Include button, "— pending" caption). Keep the aside/seam clean; NOTE:
   app.css (~:1147) and Process.razor's other references mention the "Lifecycle and Script
   Modules panel" wording — update those too.

Acceptance: all four tabs render real data for a seeded project (seed-demo); sensitive values
never rendered; preview matches a real deployment's resolution (spot-check one deployment log);
no stub alerts remain under ProjectPages except EphemeralEnvironments.
Branch: feat/project-tabs
```

#### WP9 — Retention expansion *(preamble only; rescoped; depends on D1)*

```text
TASK: Extend retention to releases, packages, and on-disk files. Rescoped 2026-07-18 — the old
prompt's two headline defects are STALE in opposite directions: runbook-run retention EXISTS
(fixed keep-50 per (runbook, environment), fired post-completion) — do NOT build a second pruning
path; and the SucceededWithWarnings gap is FIXED with a code comment in RetentionService
declaring Succeeded+SucceededWithWarnings the settled terminal-success contract — do NOT narrow
it back (add a guard test instead). Deployment-row pruning fires correctly on orchestrated
finalization and offline import. What actually remains:

1. Release retention — Octopus semantics: a release is prunable when it falls outside every
   lifecycle phase's keep-window AND has no retained deployments. Configure per lifecycle phase
   next to RetentionKeepDeployments (entity + migration + LifecycleDetail.razor editor).
   Coordinate with WP5's manual release delete — one shared code path.
2. Package retention — keep last N versions per package id (global default in a settings
   document per house rule 11; per-package override optional later). HARD CONSTRAINT: never
   delete a version pinned by a retained release's ProcessSnapshot or referenced by a retained
   deployment. Document that pruning deployments already revokes historical
   AgentPackageEntitlement (known, accepted).
3. Runbook-run keep configurability: RetentionService's keep-50 is a deliberate const with a
   keepOverride parameter hook reserved exactly for this (keep<=0 = disabled already
   implemented) — wire it to a settings-document knob + optional per-runbook override. Post-D1
   the worker prune kind-branches keep sources (D1 trap) — respect that shape.
4. FILE-STORE cleanup — the real disk-exhaustion gap: retention prunes rows via ExecuteDelete and
   DB cascades, but artifact FILES on disk are orphaned (TaskArtifact.StoredPath is only deleted
   by manual ArtifactService.DeleteAsync; the retention path never touches the store) and so are
   offline drop-bundle zips (ServerTask.DropBundlePath). Retention must delete files through the
   store abstractions when pruning parents (account-scoped paths in multi-account), or add a
   scheduled orphan-file sweep — pick one, prefer inline-delete + a safety-net sweep.
5. The absorbed log age-cap (still fully open): age-based pruning of task_step_logs blob rows +
   a sweep for orphaned task_log_live staging rows; knob in the settings document.
6. Mechanics: RetentionService is currently event-driven (post-completion) — add a NEW scheduled
   sweep job in HangfireJobRegistrar (register in BOTH the single-instance and per-account
   fan-out lists), with DRY-RUN mode default-ON behind a BuiltInFeatureCatalog flag, and an audit
   summary entry per run (counts per category).
7. Tests: reference-protection (retained snapshot pins survive), phase-window math, dry-run
   produces zero deletes, file-orphan cleanup, and the contract guard: no change narrows
   Succeeded+SucceededWithWarnings.

Acceptance: with retention enabled on seeded history, old packages/releases are pruned and their
files deleted; nothing referenced is touched; dry-run logs accurately with zero deletes; orphaned
artifact files and drop bundles from previously-pruned rows get swept; single-instance and
multi-account registrations both wired.
Branch: feat/retention-expansion
```

#### WP8 — Prompted variables *(preamble only; depends on WP1 — done)*

```text
TASK: Implement Octopus-style prompted variables (deploy-time operator input). Verified
2026-07-18: absent in every layer — zero Prompted/IsPrompted hits in src, no prompt metadata on
Variable, no promptedValues API parameter, no CLI --var. The storage is ALREADY RESERVED:
ServerTask.FormValues (jsonb) is mapped and completely inert — docs/execution-engine.md §9 lists
it as "reserved for prompted variables". Use it; do not invent new storage.

Context updates over the old prompt:
- The WP1 deploy dialog EXISTS and is MERGED: Components/Dialogs/DeployReleaseDialog.razor with
  five entry points (ReleaseDetail, Releases x2, project Dashboard, DeploymentDetail redeploy).
  It contains zero prompted-variable affordance today.
- The dialog is MULTI-TENANT: submit queues ONE DEPLOYMENT PER SELECTED TENANT. Prompted values
  are collected once but must be validated against the release snapshot's scope and stamped into
  EVERY created task's form_values.
- The injection point signature changed: DeploymentService.CreateAsync(releaseId, environmentId,
  targetId, TaskInitiator, CallerAuthorization, tenantId?, scheduledFor?, additionalTargetIds?,
  failureMode) — add a new OPTIONAL overrides parameter without disturbing the provenance
  (TaskInitiator) and RBAC (CallerAuthorization) concerns it now carries.

Scope:
1. Model: extend Variable with IsPrompted + prompt metadata (label, description, required,
   control hint: text/checkbox/select+options, sensitive). Migration per house rule 6. Prompted
   variables snapshot their DEFINITION with the release but take VALUES at deployment time.
2. Editor: variable dialogs get a "Prompt on deploy" section.
3. Deploy dialog: when the release snapshot contains in-scope prompted variables, render the
   prompts; required ones block confirm. Values flow through the new CreateAsync overrides into
   server_tasks.form_values and take HIGHEST precedence in the worker's variable resolution merge
   for that task. Sensitive prompted values: encrypt at rest via the *Encrypted convention —
   VERIFY DekRotationWalk covers encrypted members inside form_values and extend the walk if not;
   mask in logs (A2 redactor) and UI.
4. CLI parity: kraken release deploy gains --var key=value (repeatable); API: promptedValues on
   POST /api/deployments. Unknown keys rejected; missing required → 400.
5. Offline drops: a release with required prompted variables collects values at drop CREATION
   (refuse creation without them).
6. Runbook runs are OUT of scope for this WP (deploy-dialog-driven feature; runbook trigger
   surfaces can gain prompts post-v1 if demanded) — stated explicitly per the 2026-07-18 review.
7. Tests: precedence (prompted beats everything), sensitive handling, required enforcement,
   multi-tenant stamping (N tasks, same values), CLI/API round-trip.

Acceptance: define a prompted variable, deploy from the UI → prompted; value visible to steps
via Octostache; multi-tenant deploy stamps values into every task; sensitive prompted value never
appears unmasked in logs, UI, or variable preview.
Branch: feat/prompted-variables
```

#### WP7 — Triggers (scheduled deploy + scheduled runbook + auto-release-on-push) *(preamble only; depends on D1)*

```text
TASK: Implement deployment/runbook triggers. Verified 2026-07-18: there is still NO user-facing
trigger entity — ProjectPages/Triggers.razor and RunbookTriggers.razor are one-alert stubs; the
only Trigger-named type is the step-level StepStartTrigger enum; every cron in the codebase is an
internal Hangfire system job. Do NOT rebuild event-driven runbook triggering — that SHIPS today
via the subscription Runbook transport (RunbookTransport + SubscriptionEditDialog's "Runbook
trigger" option); WP7 adds the cron/scheduled and package-push kinds only.

BREADCRUMB (D1 P2 rider, 2026-07-22): the subscription Runbook transport is deliberately
single-target and always triggers with failureMode=BestEffort (indistinguishable from Atomic for
one target, so a config knob would be dead). IF anyone extends RunbookConfig to multi-target,
the SAME change MUST add failureMode to the config schema + EventSubscriptionService.
ValidateTransportConfig + SubscriptionEditDialog — three places, easy to half-do; missing it
means event-driven rolling runs silently BestEffort with no way to pick Atomic. Also remember
FailureMode is per-TRIGGER (no Runbook.DefaultFailureMode exists), so every trigger surface
plumbs it or its runs quietly default.

Step 1 — short design doc (docs/design-triggers.md, Draft): entity shape, evaluation cadence,
idempotence, multi-account. Then implement.

Scope:
1. Entity ProjectTrigger (ISpaceScoped, composite-FK per house rule 4), kinds:
   a. ScheduledDeployment: cron + IANA timezone; source = latest deployable release in a chosen
      channel; destination environment (lifecycle-legal, re-validated at fire time).
   b. ScheduledRunbookRun: cron + timezone; runbook + environment. Seam RESOLVED by D1 P2
      (2026-07-22): IRunbookTrigger.TriggerAsync now takes scheduledFor + additionalTargetIds +
      failureMode — the evaluator can persist ScheduledFor and let the unified dispatch job fire
      it (preferred), or call at fire time. If the trigger entity stores a target set /
      failure mode, plumb BOTH through (see the failure-mode BREADCRUMB in the preamble).
   c. AutoReleaseOnPackagePush: package-id filter (exact or prefix); channel; creates a release
      via ReleaseService on matching upload. VERIFIED GAP: POST /api/packages/upload calls
      PackageService.UploadAsync and returns — no hook, no event; PackageService writes NO audit
      entries and AuditEventType has no Package.* event. Add BOTH the upload-path hook AND a
      Package.Pushed audit event (subscription-visible). Reuse ReleaseService's now-enforced
      channel version rules — do not reimplement. Debounce duplicate pushes.
2. Provenance: ServerTaskCause has NO Trigger member — slot 9 is reserved only by a comment. ADD
   ServerTaskCause.Trigger = 9 plus a TaskInitiator.Trigger factory (mirror
   TaskInitiator.Subscription) and stamp cause=Trigger + cause_detail = trigger id on everything
   a trigger fires.
3. Evaluation: a new minutely Hangfire job registered in BOTH RegisterRecurringJobs and
   RegisterPerAccountRecurringJobs (the fan-out plumbing exists). Idempotence via a persisted
   last-fired watermark per trigger (missed windows: fire once, don't backfill). Cron parsing:
   whatever Hangfire ships (Cronos) — check Directory.Packages.props before adding anything.
4. Firing honors freezes, maintenance mode, and the trigger's disabled flag — enforced
   SERVER-SIDE at fire time. The only existing freeze check on the deploy path is client-side in
   DeployReleaseDialog; the worker has its own pre-start gate, but the evaluator must check
   BEFORE creating work (build a small shared server-side freeze check rather than relying on
   dispatch-time refusal). Failures are logged + audited, never crash the evaluator loop.
5. UI: fill both stub tabs with CRUD (grid + dialog: kind-specific fields, cron helper with
   next-3-occurrences preview, enable/disable, last-fired/last-result columns).
6. Audit + subscription-visible events for trigger-fired and trigger-failed.
7. Tests: cron windows, watermark idempotence (double-run fires once), freeze suppression,
   auto-release channel-rule rejection, package-push debounce.

Acceptance: a scheduled trigger deploys the latest channel release at the right local time;
pushing a matching package creates a release exactly once; both tabs fully functional;
multi-account fan-out verified by the existing smoke pattern.
Branch: feat/triggers
```

#### WP15 — Certificates library (full v1) *(preamble only)*

```text
TASK: Central certificate management (locked decision D3 2026-07-06: full scope, pre-go-live).
Verified 2026-07-18: NOTHING has landed — no Certificate entity/store/page/step/job. Three
precision corrections over the old prompt:
- Permissions are NOT in the enum. The Permission enum only carries a comment reserving the
  2300–2399 range. ADD CertificateView = 2300 and CertificateExportPrivateKey = 2301 into that
  reserved block and wire them into BuiltInRoles.
- The step-schema hook ALREADY EXISTS: StepUiSchema widget kind "certificate-ref" is defined in
  Contracts ("Picker for an X.509 certificate stored in the Kraken cert store") and currently
  falls through to a plain RadzenTextBox in StepUiSchemaForm.razor — replacing that fallback with
  a real picker is part of this WP.
- IIS starting points: KrakenIisBinding.CertThumbprint/CertStore (Contracts) and
  OctopusIisConfig's certificateVariable parsing (imported bindings carry it as null
  passthrough).

Step 1 — short design doc (docs/design-certificates.md, Draft, ~2 pages): entity shape, how cert
material rides the wire, IIS step integration, replacement/versioning. Then implement.

Scope:
1. Certificate entity (ISpaceScoped): name, uploaded PFX/PEM (private-key material encrypted at
   rest via the live DEK/KEK envelope pipeline), parsed metadata (subject, thumbprint, SANs,
   NotBefore/NotAfter, has-private-key), optional environment/tenant scoping, notes. Private key
   NEVER rendered after upload; export gated by CertificateExportPrivateKey + audited.
2. Library UI (Library nav section): expiry-sorted list with expiring-soon badges, upload dialog
   (PFX password as sensitive), detail card, archive + replace flow preserving reference identity
   (steps pick up the new version without re-editing, Octopus-style version chain). Replace the
   certificate-ref textbox fallback with the real picker.
3. IIS integration: the KrakenIis https binding can reference a library certificate as an
   alternative to a raw thumbprint; resolve the thumbprint at deploy time from the reference.
4. Certificate-typed variable support so #{MyCert.Thumbprint}-style expansion works; document the
   exposed properties (Thumbprint, Subject, NotAfter minimum).
5. Install-to-target step: new step package importing the cert (PFX + password) into a chosen
   Windows store (default LocalMachine\My), idempotent by thumbprint. Material travels the same
   protected path as sensitive variables — verify the agent wire and ensure it never lands in
   logs, step outputs, or drop bundles in plaintext.
6. Expiry notifications: a Hangfire job following the DUAL registration pattern
   (RegisterRecurringJobs + RegisterPerAccountRecurringJobs) emitting a subscription-visible
   audit event when a cert is within N days of expiry (default 30, configurable) — the existing
   Subscriptions UI/transports deliver it with no new delivery code.
7. Audit: upload / replace / archive / export / install-step usage.
8. Tests: PFX + PEM parsing, IIS binding resolution from a reference, install-step idempotence,
   expiry event emission, export permission gate, material never in logs.

Acceptance: upload a cert → reference it in an IIS https binding → deploy lands the right
thumbprint; the install step is idempotent; an expiring cert raises a subscription event; private
key never appears in UI/logs/bundles; permission gates enforced.
Branch: feat/certificates-library
```

#### WP10 — OpenTelemetry export *(preamble only; rescoped)*

```text
TASK: Wire production telemetry export (M12). Verified 2026-07-18 — the surface is smaller than
the old prompt assumed: only the two auto-instrumentation packages are wired
(AddAspNetCoreInstrumentation + AddHttpClientInstrumentation for tracing and metrics); there is
ZERO custom instrumentation anywhere (no ActivitySource, no Meter) — if domain metrics are
wanted, that is NEW scope beyond this WP, out. ConfigureResource already sets
service.name/version. Neither the Router nor the Agent carries any OTel package — per the plan's
own rule, both are OUT of scope. Logs do NOT route through OTel: the pipeline is Serilog
(UseSerilog + request logging). Production currently collects and drops traces/metrics — the
in-code comment at the OTel block says exporters come "in a later phase". This is that phase.

Scope:
1. Server only: add OpenTelemetry.Exporter.OpenTelemetryProtocol (match the 1.15.x line already
   pinned in Directory.Packages.props) and AddOtlpExporter to BOTH WithTracing and WithMetrics in
   Program.cs.
2. Config-gated from scratch (no Otel section exists in any appsettings today): Otel:Enabled +
   Otel:OtlpEndpoint + protocol (grpc/httpProtobuf) + optional headers. Disabled → exactly
   current behavior, a true no-op. The Dev-only Console exporter stays.
3. Resource attributes: service.name/version are already set — add blue-green slot/node identity
   only if cheaply available.
4. Seq leg (DECIDED 2026-07-06): logs are Serilog, so scope this as a Serilog-sink or
   Serilog→OTLP decision — decide against a LOCAL Seq container (datalust/seq), verify what its
   current version actually ingests, don't assume. Smoke: traces/metrics to a local OTLP
   collector AND logs visible in local Seq.
5. Docs: "Observability" section in docs/on-prem-guide.md — endpoint config, example compose
   collector snippet, what is exported, the data-leaves-the-host warning for regulated
   environments, AND the global-log-search statement: in-app log viewing is per-task by design;
   operators needing cross-deployment log search point the OTLP/Seq pipeline at their collector.

Acceptance: spans + metrics arrive at a local OTLP collector; logs visible in a local Seq;
disabled mode is a no-op with no startup cost; on-prem guide section written.
Branch: feat/otel-export
```

#### WP11 — Latent bug batch *(preamble only; rescoped to 6 items; depends on E-D)*

```text
TASK: Close six small, unrelated latent defects — independent items, commit separately. Rescoped
2026-07-18: the agent-update item MOVED to C6 (its multi-file-payload requirement is folded
there — do not touch AgentUpdateService here), and the server-script log-sequencing race is
FIXED (ServerScriptStepRunner routes through TaskLogService's DB-atomic allocation) — only its
regression test remains.

1. Offline-drop Email delivery: the Email case in DeploymentWorker's delivery switch still only
   LogWarnings ("Email delivery not yet implemented") and silently degrades to manual download,
   while the UI offers the channel and the SMTP stack exists (SmtpSettingsService, immediate/
   digest transports). Implement: send the drop-bundle DOWNLOAD LINK + manifest summary — do NOT
   attach the bundle. No SMTP configured → fail the delivery VISIBLY (deployment log + audit).
2. Log-sequence regression test: add a concurrent-allocation test for
   TaskLogService.AllocateSequenceRangeAsync / AppendLiveAsync (TaskLogServiceTests has only
   round-trip and blob-stitching tests today). E-D moves the counter to a task_log_counters
   table — target THAT final shape (E-D lands first; coordinate).
3. IIS auth toggles for webApplication/virtualDirectory shapes: OctopusIisConfig.MapWebApplication
   and MapVirtualDirectory never read the EnableAnonymous/Basic/WindowsAuthentication keys — only
   the webSite mapper forwards them, and the app/vdir config shapes carry no auth fields;
   IisScriptGenerator emits auth only site-level (WriteAuthenticationBlock). Map the fields on
   both shapes and emit app/vdir-level auth in the generator; extend KrakenIis tests for both.
4. security.show-error-stack-traces (BuiltInFeatureCatalog) still has no consumer. Wire it into
   the error surface (flag ON → stack trace for authorized users; OFF default → generic message)
   or REMOVE the toggle — no inert knobs.
5. Dead link: AiSettings.razor (Components/Pages/Ai/) links /docs/ai-integration.md — the file
   does not exist and nothing serves /docs. WP14 writes the doc; here, point the link at the
   canonical location WP14 will use (repo blob URL) or coordinate ordering — no 404 ships.
6. Old-chrome pages: bring root VariableSets.razor, Runbooks.razor, Tenants.razor and
   LifecycleDetail/VariableSetDetail/RunbookDetail/TenantDetail (all seven verified still on the
   older RadzenText header style) to the page-head/page-title pattern per Lifecycles.razor.

Coordinate with branch fix/ops-windows-script-encoding if still unmerged (it edits
ServerScriptStepRunner.cs / ScriptRunner.cs — C5).

Acceptance: each item verified individually (unit test or manual smoke); no regressions in
Steps.KrakenIis and Agent suites.
Branch: fix/latent-bug-batch
```

### Phase 6 — structurals pre-freeze

#### D2 — Rename the Deployment→Task wire/enum surface *(preamble + addendum; depends on D1)*

```text
TASK: Post-unification naming debt that freezes at v1. The unified spine still speaks
"deployment": DeploymentPlan, DeploymentStatus, DeploymentFailureMode; the wire field
DeploymentPlan.DeploymentId literally carries RunbookRun.Id for runbook runs (the code comment
next to it — "AgentHub resolves both tables" — is itself stale; there is one server_tasks table).
ServerTask.Status is typed DeploymentStatus and ServerTask.FailureMode is typed
DeploymentFailureMode — deployment-named enums baked into the shared spine. SEQUENCED AFTER D1
(decision 2026-07-18): the engine merge rewrites RunbookRunWorker and the dispatch surface;
renaming first would rename code D1 deletes.

Scope:
1. Rename the wire/DTO surface task-neutral: DeploymentPlan→TaskPlan, DeploymentId→TaskId,
   DeploymentFailureMode→TaskFailureMode (and the step/log/complete DTOs accordingly). Update the
   .proto, SignalR hub interfaces, agent client, REST payloads. Anchor files:
   Contracts/DeploymentContracts.cs, Contracts/IAgentHubClient.cs,
   Contracts/Offline/OfflineBundle.cs (all carry DeploymentId).
2. Collapse DeploymentStatus + ServerTaskState into ONE TaskStatus enum and delete the
   hand-written mapping in ServerTasksService. DECISION REQUIRED in-session: ServerTaskState
   carries Hangfire-only states (Scheduled, Unknown) with no DeploymentStatus counterpart
   (ServerTasksService also projects Hangfire system jobs, which are not ServerTask rows) —
   either fold them into TaskStatus or keep a thin display-only enum for Hangfire rows; state
   the choice.
3. Resolve the ServerTaskKind duplicate-name pair: the Core TPH discriminator
   (Deployment/RunbookRun) and an UNRELATED UI-projection enum in Server
   (Deployment/RunbookRun/SystemJob) share the name — merge or disambiguate as part of the
   rename pass.
4. Keep the user-facing WORD "deployment" where it is the domain concept a user deploys — this is
   the shared spine's internal/wire names, not a product rename.
5. NOTE: REST + MCP serialize enums as NAMES since 2026-07-15 — renamed enum MEMBERS are a
   wire-visible change for API/MCP/CLI consumers, not just type names. Sweep the CLI/SDK/MCP
   surface accordingly.

Acceptance: builds clean; agent and server agree on renamed contracts; a runbook run's plan no
longer carries a DeploymentId field holding a runbook id; one TaskStatus enum (with the Hangfire
decision stated); B8 round-trip green against the renamed contracts. CONTRACT CHANGE: broad
wire/enum rename — the point of the WP.
Branch: refactor/eng-task-rename
```

#### D4 — Split Server.Data → Server.Data + Server.Application *(preamble + addendum)*

```text
TASK: Server.Data is the application layer wearing a data layer's name — ~98 .cs files / ~110
class declarations under Server.Data/Services (grown from the audit's 93), plus Hangfire jobs,
envelope encryption, AI orchestration, email, and PowerShell AST analysis. Verified dependency
facts (2026-07-18): MailKit and System.Management.Automation are DIRECT PackageReferences of
Server.Data; the Anthropic dependency is NOT direct — it arrives transitively via the
ProjectReference to KrakenDeploy.Ai (cut that reference, don't hunt a nonexistent package);
Server.Data also carries a FrameworkReference to Microsoft.AspNetCore.App for the audit
interceptor's IHttpContextAccessor; Mcp → Server.Transport still exists solely for
AdhocSessionService.

Scope:
1. Create KrakenDeploy.Server.Application. Move the Services tree (including Services/Ai), Jobs/,
   Encryption/, and the email + PowerShell-AST gates there. Server.Data keeps ONLY:
   KrakenDbContext, Configurations/, Migrations/, interceptors/conventions, storage primitives,
   and whatever Settings/Spaces/Identity/Accounts infrastructure it must keep — decide and state.
2. Retarget MailKit + System.Management.Automation PackageReferences and the KrakenDeploy.Ai
   ProjectReference (the actual Anthropic carrier) to the new project. Decide where the
   AspNetCore FrameworkReference lands (the audit interceptor's IHttpContextAccessor) — moving
   the interceptor's HTTP-context dependency behind an abstraction is acceptable scope.
3. Reference graph: Server.Application → Server.Data + Server.Core; Server.Transport and Server →
   Application. Cut Mcp → Server.Transport by introducing IAdhocDispatcher in Core/Application;
   Mcp depends on the abstraction.
4. Pure move — no behavior change. Update usings + DI registration extension locations. NOTE:
   Server.Data.Tests references nearly every project (Server, Mcp, Agent, ControlPlane) —
   InternalsVisibleTo and test wiring must follow the move.

Acceptance: builds clean; Server.Data no longer references MailKit / System.Management.Automation
/ KrakenDeploy.Ai; Mcp no longer references Server.Transport; all tests green; DI boots in both
modes. CONTRACT CHANGE: none (internal structure). Coordinate with D7 (arch tests encode the new
boundaries).
Branch: refactor/split-server-data-application
```

#### D7 — Architecture-enforcement tests *(preamble + addendum; depends on D4)*

```text
TASK: Make the load-bearing layering invariants forever-true. Corrected premise (2026-07-18): ONE
architecture test already exists — SettingsBoundaryArchitectureTests, a repo-source text scan
asserting Set<Setting> appears only in SettingsService — do NOT re-create it; it also establishes
the house pattern for source-token rules. No NetArchTest/ArchUnit package is referenced anywhere.
D5-boundary assertions (ControlPlane quarantine, Router↔ControlPlane schema contract test) are
DEFERRED to the D5-FOLD package — not here.

Scope:
1. Assembly-reference tests (NetArchTest or a hand-rolled GetReferencedAssemblies check — the
   reference invariants are better tested at assembly level than text level) with today's
   verified baselines:
   - Agent + Agent.Transport: zero dependency on any Server.* assembly. IMPORTANT: Agent
     legitimately references Steps.Common (and Contracts, Execution) — a naive "Contracts+
     Execution only" assertion false-positives; allow Steps.Common.
   - Execution: Octostache-only (no ProjectReferences, no Contracts).
   - Cli: Contracts only (+ System.CommandLine). Target the PRODUCT assembly — Cli.Tests
     additionally references Server.Core and would false-fail.
   - Post-D4 assertions (this WP is sequenced after D4, so include them active): Mcp does not
     reference Server.Transport; Server.Data does not reference MailKit /
     System.Management.Automation / KrakenDeploy.Ai.
2. Keep the acceptance criterion of PROVING each rule can fail: temporarily add a forbidden
   reference locally, watch the test fail, revert.

Acceptance: arch tests pass on the current (post-D4) tree and demonstrably fail on a forbidden
reference; the existing settings-boundary test untouched. CONTRACT CHANGE: none.
Branch: test/architecture-boundaries
```

### Phase 7 — final pre-freeze

#### WP-BASELINE — Migration squash + data-correctness tests + expand/contract lint *(preamble + addendum; LAST pre-freeze, after every schema-touching WP)*

```text
TASK: Cut the v1 schema baseline. We have NO production databases anywhere — nothing to upgrade
in place — so the long tail of dev migrations has no value and real cost. This WP absorbs C4
(un-deferred 2026-07-18): the data-correctness tests only make sense against the baseline, and
the expand/contract discipline applies to every migration authored AFTER it.

Scope:
1. Squash the entire migration history into a single baseline generated from the current code
   state: one fresh initial migration + snapshot; delete the old migrations. MigrationsTests
   (apply + snapshot match) stay green. Verify the squash is faithful: schema-diff a DB created
   from the old chain against one created from the baseline — empty diff required.
2. C4's surviving scope, post-baseline:
   - Per-migration data-survival tests for every DESTRUCTIVE migration authored after the
     baseline (apply to N-1, seed old-shape rows via raw SQL, migrate to N, assert survival or
     the documented drop). Seeding old-shape rows against the pre-squash graph is explicitly NOT
     done — that graph is deleted.
   - The DekRotationCompletenessTests reflection test ALREADY EXISTS — do not redo it.
   - Expand/contract discipline for post-baseline migrations: document the rules (no rename/drop/
     NOT-NULL-without-default in the expand phase; CREATE INDEX CONCURRENTLY;
     SET NOT NULL-with-validate) and add a CI lint over new migration files enforcing them.
3. Pair with the v1 freeze checklist (a short section in this doc or docs/, listing the gates):
   wire/REST/step-name contract freeze declared; agent-JWT iss/aud validation ON (A8);
   backup→restore CI round-trip green (C1); baseline landed. Go-live does not happen with a red
   item.

Must land AFTER every schema-touching WP in phases 1–6 (D1, D3, F2, WP3, WP5, WP7, WP8, WP9,
WP15) — this is the final pre-freeze slot by design; landing it earlier re-opens the squash.

Acceptance: fresh install migrates from the single baseline to a schema byte-identical (pg_dump
--schema-only diff) with the old chain's result; CI lint rejects a rename/drop test migration;
the freeze checklist exists with owners; all suites green.
CONTRACT CHANGE: migration history (dev-only; no production DBs exist).
Branch: chore/migration-baseline
```

---

**GO-LIVE (~mid-Oct 2026)** — scope fixed, date flexes (drift from mid-Sept accepted 2026-07-18).

---

### Post-go-live

#### WP13 — Account & security feature batch *(preamble only)*

```text
TASK: Four security/account features, independent — commit separately. Anchors re-verified
2026-07-18.

1. User invites (M13.C.2): today "invite" = admin sets a temp password (UserService.InviteAsync,
   license-gated; InviteUserDialog shows the temp password). No UserInvite entity, no code, no
   /register page, no email delivery. Implement code-based invites: UserInvite aggregate
   (single-use code, expiry, optional pre-assigned teams, invited-by), admin UI on
   Configuration/Users.razor (create/revoke/resend), public registration page /register/{code}
   (anonymous route — model on Login.razor's auth opt-out; sets password, creates user, applies
   teams, consumes code), invite email via the existing SMTP transport when configured (else a
   copyable link). Audit: invited/registered/revoked (AuditEventType.UserInvited exists). Guards:
   expired/consumed codes fail closed; rate-limit the public endpoint via the existing
   fixed-window limiter mechanism in Program.cs.
2. Signing Keys UI (M13.D.1): verified nothing exists — trust still lives in static config:
   StepPackages:TrustedPublicKey (agent StepPackageLoader), Adhoc:SigningKey (server
   AdhocSigningKeyProvider), Adhoc:TrustedPublicKey (agent AdhocScriptExecutor). Create a
   SigningKey entity (purpose enum StepPackageTrust/AdhocSigning; public material; Active/
   Revoked; timestamps), a Configuration page (list/add/revoke; no private-key display after
   creation), and make verification paths read DB keys with the config keys as legacy fallback
   (deprecation warning when used). Migration path in the page help text.
3. AiCostOverride (M11.A.5.2): does not exist; rates are the hardcoded AiCostCatalog.
   SpaceAiSettings is ALREADY an ISettingsDocument (fix 7) carrying only BudgetUsdPerMonth — per
   the 2026-07-10 rider, extend THAT document with per-model rate overrides (no new entity) + a
   DbBackedAiCostCatalog decorator over the static catalog + an editor section on
   Pages/Ai/AiSettings.razor + audit on change. Budget-cap logic must pick up overridden rates.
4. Authorized live log tail — the investigation the old prompt asked for is DONE and the premise
   was wrong: there is NO live-log path to the browser today. DeploymentDetail loads the log
   exactly once in OnInitializedAsync; NO browser-side HubConnection to /hubs/ui exists anywhere
   (even the account-group pushes have zero in-app consumers — circuits use the in-process
   ITargetStatusNotifier). The four deployment:{id} group pushes (AgentHub x2 — log append +
   status change; ServerScriptStepRunner; DeployReleaseStepRunner) are dead weight with no join
   path, hence no leak today but also no authorization story. DECIDE: build or delete —
   RECOMMENDED: BUILD the real feature: hub method JoinDeployment(taskId) verifying the caller's
   account (host-derived, as OnConnectedAsync does) AND Space-level task-view permission before
   AddToGroupAsync; a DeploymentDetail live-tail subscriber consuming the pushes with clean
   ordering via the TaskLogService sequence numbers; include a cross-account AND cross-space
   authorization test. If deleting instead: remove all four pushes and the unconsumed
   IUiHubClient surface — do not leave pushes without an authorization story.

Acceptance: invite round-trip end-to-end (create → register → team applied → code dead);
signing-key rotation possible without config edits; AI rates overridable per Space and the budget
cap respects them; live tail streams to an authorized viewer and is refused cross-account/
cross-space (or the pushes are gone).
Branch: feat/account-security-batch
```

#### D5-FOLD — "SaaS revival" package *(preamble + addendum; depends on D1, D4)*

```text
TASK: One package (internal order: D5 → D6 → WP12 → blue-green fixes → boundary tests) that
takes multi-account from "deferred and entangled" to "cleanly quarantined, bootable when
revived". Multi-account stays OFF for v1 (audit decision, reaffirmed 2026-07-18); this package is
the revival gate. Sub-branches per part are fine; land behind one umbrella review.

Part 1 — D5: decouple ControlPlane/blue-green/HA from the MultiAccount flag.
- Sever the compile-time coupling: hide ControlPlane + account plumbing behind a thin
  IPlatformControlPlane (or opt-in project reference) with a null on-prem implementation, so the
  shipped on-prem binary does not compile the SaaS surface (DB-per-account provisioning,
  FileSecretStore, fleet migrator). Keep the MultiAccount:Enabled fail-fast until Part 3 removes
  it properly.
- ~~Decouple blue-green/HA from the account flag~~ — ABSORBED by BG1 (pre-go-live,
  2026-07-28, T7): topology enum, PlatformReleaseDbContext in KrakenDb, drain components in
  the OnPremBlueGreen job path, router re-pointed. Part 1 here keeps ONLY the ControlPlane
  quarantine (previous bullet) and the documentation item (next bullet). D5 starts from BG1's
  topology split — the Saas branch is already isolated behind `Topology == Saas` by then.
- Document the SaaS quarantine + revival gate.

Part 2 — D6: DbContext factory mode-dependent pooling. AddDbContextFactory<KrakenDbContext> is
Scoped UNCONDITIONALLY (the same registration method already branches on the multiAccount flag a
few lines later for the stores — the flag is in scope). On-prem: AddPooledDbContextFactory with a
fixed connection. Multi-account: keep the Scoped account-routing factory (OnConfiguring reads
IAccountContext). TWO verified obstacles the old prompt missed: (a) SpaceScopingInterceptor is
registered SCOPED and injected into the factory options — under a pooled singleton factory that
is a captive dependency; redesign it (per-context scoped accessor, or make it stateless) before
pooling; (b) the C3 EnableRetryOnFailure/KrakenDataOptions wiring lives on the factory options
and the MA OnConfiguring override re-calls UseNpgsql WITHOUT it — preserve retry in the pooled
path and mirror it in the MA path (CLI must NOT enable retry, per the KrakenDataOptions comment).
Dev-host ValidateScopes/ValidateOnBuild must pass (validation is all-env since C3).

Part 3 — WP12: per-account DEK, unblocking multi-account boot.
- Account-aware DekProvider: unwrapped DEKs keyed by accountId (ConcurrentDictionary; Guid.Empty
  = single-instance). Each account's DEK row lives in that account's tenant DB, created at
  provisioning (extend AccountProvisioner/TenantInitializer); fleet backfill CLI for existing
  accounts. Platform KEK stays config-level (one KEK, many DEKs).
- CLI: encryption rotate-dek gains --account <subdomain> and --all-accounts; rotate-kek re-wraps
  every account's DEK; remove the EncryptionCommands multi-account refusal. The per-account walk
  must cover the generic settings-document rotation step (fix 7 typed payloads).
- Remove the RunWebAsync fail-fast; boot fails CLOSED per account when a DEK is missing/
  unwrappable (that account 503s; others unaffected); never plaintext.
- P3-5 keyed caches (post-fix-7 shape): account-key SettingsService's cache + the two survivors
  (DeploymentFreezeService, LicenseUsageCounter), mirroring PerAccountOidcProviderCache.
- Fleet migrations: FleetMigrationOrchestrator.MigrateAllAsync has NO caller — wire a
  `database migrate-fleet` CLI verb (advisory lock + per-account FleetMigrationReport); the
  post-baseline migrations must reach existing tenant DBs.
Note: WP12's original prerequisite flags (A8 agent-JWT, FileSecretStore plaintext) — A8 is done;
assess FileSecretStore inside this package before any real tenant.

Part 4 — Blue-green agent-stranding fixes (2026-07-16 audit): agents pin X-KD-Release at
ENROLLMENT and reconnect to the OLD slot while it drains — new-slot dispatches fail "agent
offline" while the DB shows Online; a stopped-but-Draining slot leaves agents in an infinite 502
reconnect loop (502 is not in the 401 slow lane) AND the drain-watcher can't retire (probe
fail-safe defers) — today only a manual `releases retire` unwedges. FIX: abort agent connections
on Retire (AbortConnectionFor exists — use it); an honest "connected to another slot" dispatch
error instead of "agent offline"; evaluate the shared-registry option (registry is per-process
in-memory) and record the long-term choice.

Part 5 — the missing multi-account boundary tests (revival gate): AccountResolutionMiddleware,
CatalogAccountResolver, HostParser, AccountProvisioner currently have ZERO tests — cover them;
plus the Router↔ControlPlane schema contract test deferred from D7 (a Router.Tests fixture that
runs ControlPlane migrations and executes the two raw release-snapshot SQL queries against
app_releases/platform_settings, so a column rename fails the build, not production).
Multi-account smoke (docker-compose.smoke-multiaccount.yml) green WITH envelope encryption:
per-account round-trip + the negative test (acme's DEK cannot decrypt globex data).

Acceptance: on-prem build does not compile the SaaS surface; on-prem HA does a blue-green
upgrade WITHOUT MultiAccount:Enabled and WITHOUT stranding agents; on-prem gets a pooled context
factory passing scope validation; multi-account boots with per-account encryption, rotates
offline, and fails closed per account; boundary + registry-contract tests green.
CONTRACT CHANGE: internal structure + release-registry location; DEK schema per account — note
all in the PR.
Branch: refactor/saas-revival (sub-branches per part allowed)
```

#### WP14 — Documentation reconciliation *(preamble only; expanded scope; after C1)*

```text
TASK: Make the paperwork match the shipped product. No code changes except link fixes. All
defects below re-verified present on main 2026-07-18.

1. deploy/caddy/README.md — SAFETY-CRITICAL, but check first: C1's rider fixes the false
   "auto-applies migrations on startup" claim (Program.cs migrates only under IsDevelopment; the
   caddy compose runs Production with no kraken-init; the suggested `dotnet ef database update`
   exec is broken — no dotnet-ef in the runtime image). If C1 has landed, verify and skip; if
   not, fix it HERE first (kraken-init service like deploy/onprem, or the CLI `database setup`
   path).
2. README.md rewrite (worst offender, all verified): status line still says "M1 walking skeleton
   in progress. Not yet usable in production."; ".NET 9 SDK" prerequisite (repo is .NET 10 / SDK
   10.0.300 — same drift in docs/on-prem-guide.md, two places); repo layout lists 7 of 14 src
   projects (missing Ai, Cli, ControlPlane, Execution, Mcp, Mcp.Cli, Router); the tsvector +
   pg_trgm search row was NEVER BUILT — REMOVE entirely (locked decision D8 2026-07-06); the
   "Kraken PowerShell helper module" does not exist — relabel "planned" (locked decision D9);
   the "direct and polling transport modes pluggable" claim is stale (SignalR + OfflineDrop
   reality). Rewrite status to shipped reality (M1–M15, blue-green, multi-account behind flag,
   ~800+ tests).
3. TASKS.md: milestone content stops at 2026-05-26 (~7 weeks stale; the only later edit is the
   fleet-migration flag). The M16 section is a POINTER to this doc's §4 status column — a
   checkbox list is deliberately NOT maintained (decision 2026-07-18; this supersedes the old
   "create one checkbox per WP" instruction). Do: add post-M15 one-liner milestone entries (key
   commits) for blue-green slot deploy + Router, SaaS multi-account phases 1–3 + per-account SSO,
   Space-in-URL + isolation hardening, DB schema hardening chain fixes 1–7, extended tag sets,
   per-user API keys, envelope encryption, and the A/B/C series; tick or annotate the superseded
   M10 narrative items; fix the M10.1 slice-5 HA text (PostgresAgentConnectionRegistry + UNLOGGED
   table were REMOVED; HA = in-memory registry + sticky sessions per docs/ha-pair.md); mark M8
   delivery-channel scope honestly (Email per WP11, SFTP = file-share copy); mirror D8/D9 in the
   locked-decisions section.
4. docs/erd/: docs/db-erd.md was deleted 2026-07-10 — do NOT recreate it. The 7 PNG diagrams in
   docs/erd/ predate the server_tasks spine, composite FKs, settings fold and provenance, and
   carry no staleness marker — delete them or stamp a visible stale warning.
5. docs/mcp.md: §4 lists 8 of 10 tools — add run_adhoc_action + get_adhoc_session; fold in the
   A5 change (every read tool now permission-gated); bump to v1.2.
6. Write docs/ai-integration.md (the AiSettings page links to it; page lives under
   Components/Pages/Ai/): data-flow (what leaves the server: sanitized prompts via
   PromptSanitizer, which providers), storage (AiCallLog retention), GDPR posture (processor
   location per provider, no production payloads, operator responsibilities), budget caps +
   two-person adhoc approval. Audience: operators in regulated environments. Then fix the
   AiSettings link to a working URL.
7. docs/self-upgrade-ha.md: still Draft v0.2 — mark Archived with a pointer to
   blue-green-slot-deployment.md. docs/on-prem-guide.md: add the standard doc header.
8. NEW (production-fix §1a expansion, no status yet — build fresh): a "Coming from Octopus"
   parity/migration guide; CHANGELOG.md (seed it with §2 of the master plan, incl. the ENUM-WIRE
   contract change and F4's ApiKey:Key removal); SECURITY.md; an agent production-install guide
   (outbound firewall requirements, service/systemd install, upgrade path).
9. Status bumps per the header convention: step-packages.md + sdk-surface.md (shipped →
   Approved + version bump); saas-multi-account-architecture.md, saas-phase3-account-awareness.md,
   offline-runner.md (note implemented status).

Acceptance: a new developer reading README + TASKS.md gets an accurate picture; every doc has a
correct status header; the AI settings link resolves; the caddy guide cannot leave a production
DB unmigrated.
Branch: docs/reconciliation
```

---

#### WP16 — Script Console *(preamble only; post-go-live, placement movable)*

```text
TASK: Octopus-parity Script Console — an operator writes a one-off script BY HAND and runs it on
selected targets, with no project, release or deployment attached. Entry point: a "Script
console" button on /s/{slug}/tasks (Octopus puts it on its Tasks page — screenshot-verified
2026-07-27), plus a prefilled link from TargetDetail. This is the manual counterpart of the AI
ad-hoc flow and MUST reuse its execution path — locked decision P5 (§5, 2026-07-25): concurrency
semantics and the MaxTotalDuration budget live in AdhocScriptExecutor/AdhocScriptCommand, so the
console inherits them by construction, not by copy.

Scope:
1. Reuse the ad-hoc pipeline minus the LLM: a console run is an AdhocSession (new
   SessionKind.Console) with exactly ONE iteration whose script the operator wrote; signed via
   AdhocScriptSigner (the agent's fail-closed verify is untouched); dispatched via
   AdhocDispatcher; per-target results and the budget apply. NO generation call, NO verdict
   call, NO iteration loop, NO approval flow (the author is the runner — Octopus parity).
2. AST gate (OPEN-2 RESOLVED 2026-07-27, Domagoj: advisory): AdhocScriptGate ALWAYS runs in
   Mutating mode on console scripts, but its findings are ADVISORY — rendered inline
   ("line 3: Invoke-Expression — dynamic code execution"), and running requires an explicit
   "Run anyway" acknowledgement that is written into the audit event alongside the script
   hash and target list. ONE hard refusal is kept: Invoke-Command with a remoting parameter —
   that is not script hygiene, it breaks the console's own frozen-target-set contract.
   Rationale for advisory over hard: the blocklist cannot stop a console author anyway
   (Start-Process / bare executables pass), while hard mode false-positives on everyday LAUS
   ops work — Set-ItemProperty is THE cmdlet for IIS: drive config yet sits on the
   always-forbidden list, ditto registry quick fixes, Remove-Item -Recurse -Force on temp
   dirs, [System.IO.File]:: helpers — and ungated deployment script steps would become the
   workaround path. Implementation note: the gate API stays as-is; the console maps every
   violation kind to WARN except ForbiddenRemoting → hard refuse.
3. Target selection: individual targets; set-based by environment / target tags / tenants,
   resolved to a frozen set AT SUBMIT (dedup mandatory — F7 item 4 is a hard prerequisite).
   "Run on the Server" (ServerScriptStepRunner path) is OPTIONAL scope — arbitrary operator
   script on the server node is a bigger blast radius; include only behind the new permission,
   or defer.
4. Script body: language dropdown (PowerShell v1; bash for Linux targets); PowerShell Edition
   per run (Desktop default on Windows / Core — C5 currently defaults Desktop; add the per-run
   knob through ScriptRunner if missing); visible budget field defaulting from
   Adhoc:MaxTotalDuration (the 5-min AI default may not fit hand work — P5 note).
5. Concurrency (OPEN-1 RESOLVED 2026-07-27, Domagoj): the console gets the Octopus-parity
   per-run checkbox "Allow running concurrently with other scripts" — unchecked (DEFAULT) →
   EXCLUSIVE (write), checked → SHARED (read). Maps onto the retained
   AdhocScriptCommand.AllowParallelTaskExecution field (see F5 item 5). The AI-session flow
   stays READ-always (B2) — only console runs carry the operator's per-run choice. Rationale:
   a hand-written script has no mode gate and its author is exactly the person who does not
   think about cross-task clashes; exclusive-by-default is the safe default, the checkbox the
   informed opt-in.
6. Permission: NEW Permission.ScriptConsoleExecute — do NOT reuse AdhocActionsExecute (the AI
   flow is approval-gated; the console is not; state-institution RBAC must be able to grant
   them separately). Audit event per run: script hash, frozen target list, initiator.
7. Tasks page: console runs appear as rows — ServerTasksService today aggregates deployments +
   runbook runs + Hangfire only; add console/ad-hoc sessions with a DetailUrl to a run detail
   view (per-target stdout/stderr/exit code). They must also surface via the F6 ?target=
   filter.

Acceptance: round trip on 2 targets with per-target output and exit codes visible; signature
tamper test refuses on the agent; budget expiry kills the tree and reports; a set-resolved
duplicate target dedups; permission denial without ScriptConsoleExecute; console run visible on
/tasks and under /tasks?target=; concurrency tested in BOTH modes (default/unchecked console run blocks and is blocked like a
writer — e.g. it waits for a running deployment wave; checked console run co-runs with Shared
work and other checked console runs). Gate: a script containing Invoke-Expression surfaces the
finding and runs only after explicit acknowledgement, which lands in the audit event;
Invoke-Command -ComputerName is hard-refused with no acknowledgement path.
Branch: feat/script-console
```

## 7. History

| Version | Date | Change |
|---|---|---|
| 2.0 | 2026-07-28 | **Topology & on-prem blue-green.** New §5 block "Resolved 2026-07-28" (T1–T9; supersedes D-bg-5): `Deployment:Topology` enum (OnPrem default / OnPremBlueGreen / Saas, installer-level), blue-green available on-prem (3 slots, 1..N servers, router = entry point), release registry → `PlatformReleaseDbContext` (KrakenDb `platform` schema on-prem / catalog on SaaS — text file rejected: no cross-process transactions), non-additive migrations = stop-the-world with a `database --upgrade` guard, expand/contract lint now MANDATORY. New WP **BG1** (L, pre-go-live, after F7); D5-FOLD Part 1 rescoped (BG decoupling absorbed); WP-BASELINE deps + lint scope updated; execution order refreshed (T9). Origin-sync banner retired. Motivated by Octopus research: their HA upgrade is a full-cluster outage, mixed versions forbidden, no server blue-green exists — this is a differentiator. |
| 1.5 | 2026-07-27 | CI: both SaaS-path smokes (multi-account, blue-green) marked `continue-on-error` — both fail solely on the M13.D.2 "envelope encryption does not support MultiAccount" boot fail-fast, and blue-green cannot be reconfigured around it (multi-account-only by D-bg-5). Removing both flags added to WP12/D5-FOLD acceptance. Corrects an earlier claim in `d140993` that blue-green ships in v1 — it does not; single-node on-prem upgrades stop → migrate → start. |
| 1.4 | 2026-07-27 | WP16 OPEN-2 resolved: ADVISORY Mutating-mode gate on console scripts (findings inline, explicit acknowledged "Run anyway" recorded in the audit event; sole hard block = Invoke-Command with a remoting target, which breaks the frozen-target-set contract). WP16 has no open decisions left. |
| 1.3 | 2026-07-27 | WP16 OPEN-1 resolved: console adopts the per-run concurrency checkbox (unchecked → WRITE default, checked → READ); F5 item 5 flipped from "drop the wire field" to "retain + map" with the skew rationale. OPEN-2 (gate for hand-written scripts) still open. |
| 1.2 | 2026-07-27 | Added **WP16** (Script Console — Tasks-page entry, reuses the ad-hoc execution path; OPEN-1 concurrency checkbox vs READ-always, OPEN-2 gate for hand-written scripts). Screenshot-verified Octopus parity: console lives on the Tasks page and its per-run concurrency DEFAULTS TO EXCLUSIVE — nuances the B2 evidence (issue #5853 concerned system scripts, not the console). F5 item 5 gains the OPEN-1 caveat (field retention). |
| 1.1 | 2026-07-25 | F2 closed (followups 1–10; item 4 re-resolved). Added §5 decision log 2026-07-25 and Phase-4 WPs **F5** (agent reader-writer gate + updater write-lock), **F6** (server-side per-plan target exclusion at claim time — mutual-consent model, project/runbook consent flags, FIFO by overlap, reason string, /tasks?target filter), **F7** (parallel-safety-audit riders incl. secrets-in-%TEMP% GDPR fix). All three scheduled pre-go-live; F6 added to WP-BASELINE dependencies. |
| 1.0 | 2026-07-18 | Initial version. Unifies finish-plan-2026-07-05 (v1.3) + production-fix-prompts-2026-07-13 (v1.1); folds in the 2026-07-16 execution-engine audit (E-series, D1 merge design) and the 2026-07-18 10-agent code verification of every open WP; adds F1–F4, WP-BASELINE, D5-FOLD; archives both originals. |

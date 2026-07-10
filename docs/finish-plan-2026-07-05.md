# KrakenDeploy — Finish-Line Plan & Opus 4.8 Work Prompts

| | |
|---|---|
| Version | 1.2 |
| Date | 2026-07-10 |
| Authors | Domagoj Jugović, Claude (5-agent audit) |
| Status | Review |
| Technologies | .NET 10, Blazor Server, Radzen, EF Core 10, PostgreSQL, SignalR, gRPC, Hangfire |
| Scope | Whole repo @ HEAD `c6cb01c` |

Audit method: five parallel review agents (render modes / CRUD coverage / TASKS.md-vs-code / Octopus parity / security & multi-account), findings cross-verified against source. Headline claims (no deploy button, stub tabs) were independently re-confirmed by grep/read before this document was written.

---

## 1. Audit summary

### 1.1 What is genuinely broken or missing (verified in code)

**Core product**
- **The browser cannot start a deployment.** `DeploymentService.CreateAsync` + `POST /api/deployments` (`Program.cs` ~1856) are consumed only by CLI/MCP. No `.razor` file calls them — `Home.razor`'s "Deploy" button just navigates to `/projects`. There is also no cancel (nothing ever sets `DeploymentStatus.Cancelled`) and no redeploy.
- **Manual intervention is a no-op.** `steps/KrakenDeploy.Steps.Manual/ManualInterventionStepHandler.cs` logs the instructions and auto-approves. No paused status, no approve/reject UI. `InterruptionView` permission is reserved but unused.
- **7 project tabs are "— pending" stubs** wired into `ProjectShell.razor:91-123`: Triggers, Runbooks (project-scoped), Runbook Triggers, Ephemeral Environments, All Variables, Tenant Variables, Variable Preview. Plus the disabled "Script Modules" card in `ProjectPages/Process.razor:345` and the fake `RegenerateAsync` toast in `DeploymentDetail.razor:552` (`await Task.CompletedTask`).
- **Backend exists, UI unreachable:** Step Packages pages have no nav entry or inbound link; tenant edit (`TenantService.UpdateAsync` + `PUT /api/tenants/{id}`), tag-set/tag rename, tag↔target assignment (6 service methods + 5 endpoints, `Program.cs:2163-2231`), step-template edit, runbook rename — all service+endpoint complete with zero UI callers. `DeploymentFailureMode` (BestEffort/Atomic) is API-only.
- **Missing end-to-end (no backend either):** target delete, release delete, project-group rename/delete, user edit/disable.

**Octopus parity (top gaps by impact, intentional deviations excluded)**
1. Manual intervention/approvals (above).
2. Triggers — none: no scheduled deploy/runbook triggers, no auto-release-on-package-push (only one-shot `ScheduledFor`).
3. Prompted variables — absent in every layer.
4. External feeds (Docker/NuGet/Maven) — documented deferral (`TASKS.md:806`); blocks container workflows.
5. Machine policies / health-check scripts — only heartbeat Online/Offline.
6. Retention prunes deployment rows only — packages, releases, runbook runs grow unbounded.
7. Lifecycle auto-promotion — every environment hop is manual.
8. Certificates library — permissions reserved, entity never landed; IIS HTTPS relies on raw thumbprints.
9. Non-AI script console — `/adhoc` is LLM-gated, PowerShell-only.
10. Release version templates/auto-increment — version is a manually typed string.

**Ops / latent bugs (still open from earlier audits)**
- OTel export missing (M12): instrumentation real, production collects and drops telemetry.
- Offline-drop Email delivery logs "not yet implemented" (`DeploymentWorker.cs:860`) while the UI offers the channel; SMTP infra now exists (M13.B), so this is now cheap.
- Agent auto-update swaps only the exe out of the staging dir (`AgentUpdateService.cs:267`).
- Server-script log sequencing: unguarded `NextLogSequence++` outside `LogSequencer` (`ServerScriptStepRunner.cs:246`) — duplicate sequence numbers possible with parallel server steps.
- IIS auth toggles ignored for `webApplication`/`virtualDirectory` shapes (`OctopusIisConfig.cs:218,287`); `webSite` shape works.
- `security.show-error-stack-traces` feature toggle has no consumer.
- Dead link: `AiSettings.razor:27` → `docs/ai-integration.md` (file doesn't exist, nothing serves `/docs`).
- Old-chrome pages: root VariableSets/Runbooks/Tenants + LifecycleDetail/VariableSetDetail/RunbookDetail/TenantDetail.

**Multi-account SaaS**
- Feature-complete and smoke-verified, BUT the web host **fail-fasts on boot** when `MultiAccount:Enabled` because `DekProvider` (envelope encryption) is single-instance-only (`Program.cs:263-270`, `EncryptionCommands.cs:323`). Per-account DEK is the one real blocker.
- P3-5 keyed-cache optimization open (Scoped-in-M-A is leak-safe but per-request).
- Latent: `deployment:{id}` UiHub group pushes have **no join path** — harmless today, becomes a log-stream IDOR if a join is ever added without per-account/space authorization.
- Verified FIXED (was thought open): gRPC package/step-package entitlement (`AgentPackageEntitlement`), channel `AccountId` carry, reserved subdomains, root-scope sweep, SignalR `Clients.All`.

**Render modes** — the historical "page renders static SSR, buttons dead" class is structurally fixed: `App.razor` applies `InteractiveServer` globally; only Login/Error opt out (both handler-free). No action needed; do not add per-page `@rendermode` anymore.

**Docs rot**
- README: claims "M1 in progress", ".NET 9", dual transport modes, tsvector search, Kraken PowerShell module — all false; repo layout lists 7 of ~15 projects.
- TASKS.md stops at M15 — ~6 weeks of shipped work (blue-green, multi-account, Router, API keys, envelope encryption, failure modes…) untracked; dozens of stale-unchecked boxes; HA slice 5 describes a removed implementation.
- `docs/db-erd.md` ~20 migrations behind; `docs/mcp.md` lists 8 of 10 tools; `self-upgrade-ha.md` superseded by blue-green doc; `on-prem-guide.md` missing the status header.
- Two "locked decisions" were never built: Postgres tsvector search, `Kraken` PowerShell module → decide build-or-descope (§3).

### 1.2 What the audit confirmed as done and healthy
Projects/groups, process editor (M14/M15 exceeds Octopus), step templates + community sync, built-in feed + Octodiff, environments, tenants + tags, variables engine (scoping, sensitive, envelope encryption), lifecycles/channels (manual promotion), runbooks core, audit log + export + retention, users/teams/roles/permissions, Spaces hard isolation, per-user API keys, subscriptions (email/webhook/runbook/AI), freezes, backup/restore incl. per-account, blue-green self-hosting, offline drops (minus email), MCP + AI layer, analytics pivot, projects dashboard.

---

## 2. Plan

**Execution parameters (locked 2026-07-06):** on-prem single-instance ships first, SaaS follows. Scope is fixed; the date flexes — realistic go-live is **~2.5 months** at ~28 working days of build plus review buffer. **One Opus session at a time, one WP per session on its own branch, `/code-review` before every merge.** Progress tracked as a new **M16 milestone in TASKS.md** (create the checklist at kickoff — one checkbox per WP below). WP3, WP7, WP12 and WP15 are design-doc-first (built into their prompts). Worker/agent-touching WPs (WP2, WP3, WP7, WP15) must not interleave with anything else touching `DeploymentWorker`.

**Ordering update (2026-07-10):** WP1 and WP2 are **DONE** (merged to main). Before WP3, the **DB schema hardening chain** runs to completion — `docs/db-schema-fix-prompts-2026-07-10.md`, fixes 1–7 on `fix/db-schema-hardening`. It rewrites `deployments`/`runbook_runs` into a unified `server_tasks` spine, replaces the settings tables with a `SettingsService`-backed document table, enforces channel version rules, and adds composite Space FKs — surfaces that WP3, WP7, WP8, WP9, WP12, WP13 and WP15 build on. Merged execution order: **schema fixes 1→7 → WP3 → WP4 (re-audit first) → WP5 → WP6 → WP9 → WP8 → WP7 → WP15 → WP10 → WP11 → GO-LIVE → WP13 → WP12 → WP14**. Affected WP prompts below carry a `RIDER (2026-07-10)` paragraph — paste riders together with the prompt.

Sizes: S < ½ day, M ≈ 1 day, L ≈ 2–3 days, XL ≈ 1 week (Opus-assisted).

Pre-go-live, in execution order (single serial track):

| # | WP | Title | Size | Notes |
|---|---|---|---|---|
| 1 | WP1 | Deploy from the web UI (full dialog: env + tenant + schedule + FailureMode) | L | **DONE** — merged 2026-07-10 |
| 2 | WP2 | Cancel (boundary model) / redeploy / fix fake Regenerate | M | **DONE** — merged 2026-07-10 |
| 3 | WP3 | Real manual intervention (pause/approve/reject) | XL | **go-live blocker**; spec in prompt |
| 4 | WP4 | Reachability + missing edit affordances batch | M | |
| 5 | WP5 | Missing CRUD end-to-end (target/release/group/user) | M | |
| 6 | WP6 | Finish project tabs (variables trio + runbooks tab) | L | ephemeral stub stays (D7) |
| 7 | WP9 | Retention expansion | L | on-prem disk guard — don't defer |
| 8 | WP8 | Prompted variables | L | needs WP1 dialog |
| 9 | WP7 | Triggers — all three kinds in one WP | XL | |
| 10 | WP15 | Certificates library — full v1 incl. install-to-target step | XL | new (D3); prompt in §5 |
| 11 | WP10 | OpenTelemetry export (OTLP + documented/smoked Seq pipeline) | M | |
| 12 | WP11 | Latent bug batch (email delivery, agent update, log race, IIS, chrome) | M | production-relevant — pre-go-live |

**— GO-LIVE —**

| # | WP | Title | Size | Notes |
|---|---|---|---|---|
| 13 | WP13 | User invites, signing-keys UI, AiCostOverride, SignalR group hygiene | L | |
| 14 | WP12 | Per-account DEK → unblock multi-account boot + P3-5 caches | XL | committed slot (SaaS follows on-prem) |
| 15 | WP14 | README / TASKS.md / ERD / mcp.md / ai-integration.md reconciliation | M | final pass; M16 ticks are continuous |

---

## 3. Decision list — RESOLVED 2026-07-06 (grill session with DJ)

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

Also locked in the same session: WP3 approval model (step-defined responsible teams; self-approval allowed; per-step auto-fail timeout, global default 72 h), WP2 boundary-cancel model, WP7 all three trigger kinds in one WP, WP10 verified against Seq in addition to neutral OTLP, tracking via TASKS.md M16, `/code-review` gate on every merge, single serial Opus track, scope fixed / date flexes.

---

## 4. How to use the prompts

Each work package below is a self-contained prompt for a fresh Claude Code session on **Opus 4.8**. Paste the **Common preamble** first, then the WP prompt. One WP per session/branch.

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
    (an architecture test enforces this).
```

---

## 5. Work-package prompts

### WP1 — Deploy from the web UI

```text
TASK: Make deployments startable from the browser. Today DeploymentService.CreateAsync
(src/KrakenDeploy.Server.Data/Services/DeploymentService.cs) and POST /api/deployments
(src/KrakenDeploy.Server/Program.cs ~line 1856) are used only by the CLI and MCP — no Blazor
component calls them. This is the single biggest product gap: an Octopus clone whose UI cannot deploy.

Scope:
1. New DeployReleaseDialog under Components/Dialogs:
   - Environment picker limited to lifecycle-legal environments for the release's channel/phase.
     First locate where the channel/lifecycle gate is enforced for POST /api/deployments (endpoint
     vs service) — the UI path must go through the SAME server-side enforcement; surface violations
     as dialog validation, not post-hoc toasts.
   - Tenant picker for tenant-associated projects (target_tenants M2M is THE association).
   - Optional "schedule for later" (Deployment.ScheduledFor + ScheduledDeploymentDispatchJob exist).
   - DeploymentFailureMode selector: BestEffort (default) / Atomic. The enum, entity column,
     service parameter and API field exist since commit 79b9230; zero UI references today. Add a
     short inline explanation of the two modes.
   - Read-only preview of the targets that will be hit (environment + role matching) before confirm.
2. Entry points: primary Deploy button on Components/Pages/ProjectPages/ReleaseDetail.razor;
   deploy affordance on ProjectPages/Dashboard.razor matrix rows and Pages/Releases.razor rows;
   make Home.razor's Deploy button (currently just navigates to /projects) lead into this flow.
3. Call DeploymentService directly from the dialog (server-side Blazor) — no HTTP self-calls.
   UiActionGuard re-check of the deployment-create permission in the confirm handler. Respect
   deployment freezes (DeploymentFreezeService) exactly like the API path. On success navigate
   to /s/{slug}/deployments/{id}.
4. Audit entry for UI-initiated deployments if DeploymentService doesn't already write one.

Acceptance:
- From the browser: create a deployment to a lifecycle-legal environment (with tenant where
  applicable), choose Atomic, land on the live deployment detail page.
- Illegal environment choices are prevented in the dialog AND rejected server-side if forced.
- Freezes block with a clear message.
- Existing CLI/MCP/API deployment paths unchanged; affected tests green.
```

### WP2 — Deployment cancel, redeploy, and the fake Regenerate button

```text
TASK: Give deployments lifecycle controls in the UI, and remove a fake button.

1. Cancel, end-to-end. DeploymentStatus.Cancelled exists but nothing sets it.
   - Implement DeploymentService.CancelAsync. Pending/queued: mark Cancelled and ensure
     DeploymentWorker's dequeue path skips cancelled items (trace the TenantWorkItem channel
     consumer in src/KrakenDeploy.Server.Transport/DeploymentWorker.cs).
   - Running: investigate what cancellation the worker/agent protocol supports today
     (CancellationToken flow to step runners / AgentHub). If in-flight step abort needs agent
     protocol changes, implement cancel-at-next-step-boundary (worker re-checks status between
     steps/waves) and state the limitation in the UI tooltip. Do NOT redesign the agent protocol.
   - Wire POST /api/deployments/{id}/cancel + audit entry. Terminal-status interaction: check
     DeploymentTerminalStatusResolver so Cancelled isn't overwritten by BestEffort/Atomic logic.
2. UI: Cancel button on Pages/DeploymentDetail.razor (visible for Pending/Running,
   UiActionGuard-gated, confirm dialog) and a row action on Pages/Deployments.razor.
3. Redeploy button on DeploymentDetail → opens the DeployReleaseDialog from WP1 prefilled
   (same release/environment/tenant). If WP1 is not merged in this branch, skip redeploy.
4. Fix the fake button: DeploymentDetail.razor RegenerateAsync (~line 552) shows a
   "Re-generating…" toast then does nothing (await Task.CompletedTask). Find how the offline
   drop bundle is generated at dispatch (DropBundleService) and actually regenerate the bundle;
   if regeneration is genuinely not meaningful, remove the button entirely. No fake affordances.

Acceptance: cancelling a pending deployment prevents dispatch; cancelling a running one stops at
the documented boundary with terminal status Cancelled (audited); Regenerate produces a fresh
downloadable bundle or is gone; worker tests (OrchestratorTestHarness-based) cover the new paths.
```

### WP3 — Real manual intervention (pause / approve / reject)

```text
TASK: Replace the auto-approving manual-intervention step with a real pause/approve/reject flow.
Today steps/KrakenDeploy.Steps.Manual/ManualInterventionStepHandler.cs logs the instructions and
approves; TASKS.md:1670 admits interactive intervention "doesn't exist yet". Target market is
state-sector change control — this is the top parity gap.

Step 1 — design doc first: write docs/design-manual-intervention.md (header: version/date/author/
status Draft) covering the decisions below; keep it to ~2 pages; then implement.
- Interruption aggregate (ISpaceScoped): DeploymentId, StepId, instructions, responsible team(s),
  status Pending/Approved/Rejected, acted-by, notes, UTC timestamps.
- DECIDED requirements (2026-07-06, do not re-litigate): approvers = step-defined responsible
  team(s), Octopus-style — the step editor gets a team multiselect; empty list = anyone holding
  the intervention permission in the Space. Self-approval IS allowed (the deployment initiator
  may approve their own intervention). Timeout: per-step optional auto-fail timeout with a
  global default of 72 hours — expiry fails the deployment exactly like a rejection (cleanup
  steps honored) with an audit entry noting timeout rather than human rejection.
- Pause semantics: study DeploymentWorker wave orchestration, DeploymentTerminalStatusResolver,
  and StepConditionEvaluator (worker feeds it per-target vs global signals) before choosing
  between a new DeploymentStatus (e.g. AwaitingIntervention) or a parallel flag. The worker must
  persist state and FREE its slot (no thread parked on a paused deployment); resume re-enqueues
  via the TenantWorkItem channel (carry AccountId — house rule 5).
- Intervention is deployment-global: pause before the step's wave dispatches, not per-target.
- Offline drop bundles keep current log+auto-approve, with an explicit warning line in the bundle log.

Step 2 — implementation:
- Permissions: use the reserved InterruptionView in the Permission enum; add a take/respond
  permission if the enum lacks one. RBAC-gate approve/reject; record approver identity.
- UI: banner + Approve/Reject dialog (notes mandatory on reject) on Pages/DeploymentDetail.razor;
  pending-intervention indicator on Pages/Deployments.razor and the Tasks page.
- Events: audit entries + a subscription-visible event type so M13.B email/webhook subscriptions
  can notify approvers (see Domain/Subscriptions/EventSubscription.cs filtering).
- Rejection → deployment fails cleanly; Failure/Always-conditioned cleanup steps still run per
  DeploymentFailureMode semantics.
- Tests: pause/resume/abort via OrchestratorTestHarness; permission gating; rejection cleanup.

Acceptance: a process containing a Manual Intervention step pauses at that step with instructions
visible; an authorized user approves (deployment continues) or rejects with notes (deployment
fails cleanly); everything audited and notifiable; deployments without the step are unaffected.

RIDER (2026-07-10, after the DB schema chain): deployments and runbook runs are now ONE
server_tasks table (kind discriminator) and the worker/AgentHub were rewritten in schema fix 3 —
re-locate the wave orchestration and terminal-status resolver before designing. The pause
status/flag lives on the unified spine (decide whether interventions apply to runbook-run kind
too — recommend yes). The Interruption entity follows house rule 4's composite-FK convention.
Type/table names cited in this prompt may have changed — trust the code, not these identifiers.
```

### WP4 — Reachability + missing edit affordances (batch)

```text
TASK: Wire existing, tested backend surface into the UI. Every item below has a working service
method and (usually) a REST endpoint with ZERO UI callers. All handlers: UiActionGuard re-check;
audit entries where the service doesn't already write them; follow the page-head chrome pattern.

1. NavMenu (Components/Layout/NavMenu.razor): add "Step Packages" under the Library section →
   /s/{slug}/step-packages. The complete StepPackages.razor + StepPackageUsagePage.razor feature
   (list/upload/uninstall/bulk-upgrade/usage) is currently reachable only by typing the URL.
2. Tenant edit: Pages/TenantDetail.razor has no edit — wire TenantService.UpdateAsync
   (rename/description; see PUT /api/tenants/{id}, Program.cs ~2098) via an edit dialog.
3. Tag-set / tag rename: TenantDetail creates and deletes only — wire UpdateTagSetAsync /
   UpdateTagAsync (PUT /api/tag-sets/{id}, PUT /api/tags/{id}).
4. Tag↔target assignment UI: TargetDetail.razor has zero tag references while the backend has
   AddTagToTargetAsync / RemoveTagFromTargetAsync / GetTagsForTargetAsync (+ endpoints
   Program.cs:2210-2231). Add a "Tenant tags" card on the target detail page (Octopus-style
   multiselect). Note: target_tenants direct M2M stays THE primary association; tags are auxiliary.
5. Step-template edit: Pages/StepTemplates.razor has create/import/delete but no edit —
   wire StepTemplateService.UpdateAsync (PUT /api/step-templates/{id}, Program.cs ~1438),
   reusing the creation dialog prefilled.
6. Runbook rename/description: Pages/RunbookDetail.razor — wire RunbookService.UpdateAsync
   (PUT /api/runbooks/{id}, Program.cs ~2368).
7. Fix the dead link Pages/Ai/AiSettings.razor:27 → "/docs/ai-integration.md" (404: file absent
   and nothing serves /docs). Point it at the repo GitHub blob URL for docs/ai-integration.md
   (the doc itself is another WP) or remove the link if you cannot determine the canonical URL.
8. StepPackageUsagePage.razor:107 drill-through targets the stub project-runbooks tab — retarget
   to the global Runbooks page filtered by project until the project tab is real.

Acceptance: each operation performable in the browser; no regressions on the touched pages;
build + affected tests green; quick manual smoke of each page listed.

RIDER (2026-07-10): the extended tag sets rework (merged 2026-07-10) rebuilt the tag UI
(TagSets/TagSetDetail pages, EntityTagEditor, TagChip; TargetDetail and TenantDetail touched)
and endpoints moved. Re-audit items 2-4 and every cited Program.cs line number before starting —
several items may already be done or relocated. Re-check item 1 too (NavMenu gained a Tag Sets
entry in the same rework; verify whether Step Packages followed).
```

### WP5 — Missing CRUD end-to-end (backend + UI)

```text
TASK: Add the destructive/administrative operations that are missing on BOTH sides (no service
method, no endpoint, no UI). Design each guard before coding; these are destructive — prefer
refusal over cascade surprises. All: permission re-check via UiActionGuard, confirm dialogs
that state consequences, audit entries, tests.

1. Target delete/decommission. Investigate the FK graph first (deployments reference targets via
   legacy TargetId + deployment_target_assignments; heartbeats; agent identity; package
   entitlement reads deployment history). SeedDemoCommands.cs:645 has teardown logic to learn
   from. Recommendation: soft-delete/retire (hidden from matching+dispatch, history preserved);
   hard-delete only when the target has no deployment history. Agent connection for a retired
   target must be rejected at AgentHub connect.
2. Release delete: ReleaseService has no DeleteAsync. Block when deployments reference the
   release (or require an explicit force that also explains retention interplay). Snapshot rows
   (process/variables) go with it. Note: the blue-green *server release registry* under
   src/**/Releases/ is a DIFFERENT subsystem — do not touch it.
3. ProjectGroup rename + delete: ProjectService only has CreateGroupAsync. Delete only when
   empty, or offer "move projects to Default Project Group" (CreateAsync already defaults it).
4. User edit/disable: display-name edit + enable/disable via Identity lockout. Guards: cannot
   disable yourself; cannot disable/downgrade the last user holding AdministerSystem. Disabled
   users' circuits should not survive (check how session invalidation works with Blazor Server —
   at minimum, document the latency).

Acceptance: each operation works from the browser with correct guards; deleting/retiring never
breaks existing deployment history pages; build + tests green (add service-level tests per item).

RIDER (2026-07-10, after the DB schema chain): legacy deployments.target_id is GONE (fix 2) —
item 1's FK-graph note is stale; execution history references targets only via
task_target_assignments and step outcomes, both RESTRICT. Retire/soft-delete is therefore the
ONLY path for targets with history (hard delete works only for history-free targets). Item 2:
deployments are server_tasks rows (kind=Deployment) and server_tasks -> releases is RESTRICT.
Item 4 complements fix 4's DB-level user-delete cascades (api keys, team memberships,
per-user views).
```

### WP6 — Finish the project tabs (variables trio + runbooks)

```text
TASK: Replace four "— pending" stub tabs under ProjectShell with real pages, and remove one
permanent placeholder. The variable resolution engine already exists — this is mostly read-model
UI over VariableService.

1. ProjectPages/AllVariables.razor: aggregated read-only view — project variables + linked library
   sets + tenant overlays, with columns for scope (env/target/role/channel/tenant) and a source
   link (project vs named library set). Study VariableService.ResolveAsync (used by the worker)
   and the snapshot builder to reuse, not reimplement, precedence logic.
2. ProjectPages/VariablePreview.razor: pick environment (+ tenant/target/channel as applicable) →
   show the RESOLVED variable set exactly as a deployment would see it (sensitive values masked),
   including which definition won and why (specificity). Reuse ResolveAsync.
3. ProjectPages/TenantVariables.razor: per-tenant variable values for this project. First check
   how tenant variable values are modelled today (the resolver overlays tenant values;
   VariableSetKind.TenantCommon is "reserved — no creation path wired"). Scope: project↔tenant
   values only; do NOT invent a TenantCommon authoring flow — if the model only supports
   overlay-read, build the editor on whatever entity the resolver actually reads, and say so.
4. ProjectPages/Runbooks.razor: real project-scoped runbook list (global Pages/Runbooks.razor and
   RunbookService exist — filter by project) with inline create, matching the global page's
   affordances.
5. ProjectPages/Process.razor:345-356 "Script Modules" card: remove the permanently-disabled
   placeholder card (script modules are not scheduled). Keep the code seam clean for later.

Acceptance: all four tabs render real data for a seeded project (use seed-demo), sensitive values
never rendered, preview matches what a real deployment resolves (spot-check one deployment log),
no stub alerts remain under ProjectPages except EphemeralEnvironments (separate product decision).
```

### WP7 — Triggers (scheduled + auto-release-on-push + runbook schedules)

```text
TASK: Implement deployment/runbook triggers. Today there is NO trigger entity — both
ProjectPages/Triggers.razor and RunbookTriggers.razor are "pending" stubs; the only scheduling is
one-shot Deployment.ScheduledFor via ScheduledDeploymentDispatchJob (use it as the reference for
dispatch mechanics, freezes, and account fan-out).

Step 1 — short design doc (docs/design-triggers.md, status Draft): entity shape, evaluation
cadence, idempotence, multi-account. Then implement.

Scope:
1. Entity ProjectTrigger (ISpaceScoped), kinds:
   a. ScheduledDeployment: cron + IANA timezone; source = latest deployable release in a chosen
      channel; destination environment (lifecycle-legal, validated at fire time too).
   b. ScheduledRunbookRun: cron + timezone; runbook + environment.
   c. AutoReleaseOnPackagePush: package-id filter (exact or prefix); channel; creates a release
      via ReleaseService when a matching package version is pushed (hook the PackageService
      upload path; respect channel version rules; debounce duplicate pushes).
   Machine-event triggers are OUT of scope.
2. Evaluation: a Hangfire recurring job (minutely) registered in HangfireJobRegistrar — follow the
   existing per-account fan-out pattern (PerAccountRecurringJobRunner) so multi-account keeps
   working. Idempotence via a persisted last-fired watermark per trigger (missed windows: fire
   once, don't backfill). Cron parsing: prefer what Hangfire already ships (Cronos) — no new deps
   without checking Directory.Packages.props.
3. Firing honors: deployment freezes, maintenance mode, disabled flag on the trigger. Failures
   are logged + audited, never crash the evaluator loop.
4. UI: fill both stub tabs with CRUD (grid + dialog: kind-specific fields, cron helper with
   next-3-occurrences preview, enable/disable toggle, last-fired/last-result columns).
5. Audit entries + a subscription-visible event for trigger-fired and trigger-failed.
6. Tests: cron evaluation windows, watermark idempotence (double-run of the evaluator fires once),
   freeze suppression, auto-release channel-rule rejection.

Acceptance: a scheduled trigger deploys the latest channel release at the right local time;
pushing a matching package creates a release exactly once; both tabs fully functional; multi-
account fan-out verified at least by the existing smoke pattern.

RIDER (2026-07-10, after the DB schema chain): (a) channel version rules are now ENFORCED by
ReleaseService (schema fix 7 Part C) — consume that validation in AutoReleaseOnPackagePush, do
not reimplement it; (b) server_tasks.cause exists (fix 6) with a reserved Trigger value — stamp
cause=Trigger (+ cause_detail = trigger id) on everything a trigger fires; (c) global trigger
defaults/knobs go into a settings document per house rule 11 (per-trigger rows stay a real
entity, composite-FK per house rule 4); (d) executions are server_tasks — ScheduledFor and the
dispatch job operate on the unified table.
```

### WP8 — Prompted variables

```text
TASK: Implement Octopus-style prompted variables (deploy-time operator input). Currently absent
in every layer (zero "Prompted" hits in src/).

Scope:
1. Model: extend Variable (Domain/Variables/) with IsPrompted + prompt metadata (label,
   description, required, control hint: text/checkbox/select+options, sensitive). Migration per
   house rule 6. Snapshot: check how release variable snapshots store values — prompted variables
   must snapshot their DEFINITION but take VALUES at deployment time.
2. Editor: variable dialogs on VariableSetDetail.razor / ProjectPages/Variables.razor get a
   "Prompt on deploy" section.
3. Deploy dialog (from WP1): when the release's snapshot contains prompted variables in scope,
   render the prompts; required ones block confirm. Values flow into DeploymentService.CreateAsync
   as an overrides dictionary and take highest precedence in resolution for THAT deployment
   (find the right injection point in the worker's variable resolution — likely where tenant/
   output overlays merge). Sensitive prompted values: encrypt at rest like other sensitive
   variables (AES-GCM envelope pipeline), mask in logs and UI.
4. CLI parity: kraken release deploy gains --var key=value (repeatable); API: promptedValues on
   POST /api/deployments. Validation: unknown keys rejected; missing required → 400.
5. Offline drops: creating a drop for a release with required prompted variables must either
   collect values at creation or refuse with a clear message — pick collecting at creation.
6. Tests: precedence (prompted beats everything), sensitive handling, required enforcement,
   CLI/API round-trip.

Acceptance: define a prompted variable, deploy from the UI → prompted for it, value visible to
steps (Octostache substitution), sensitive prompted value never appears in logs or the variable
preview unmasked.

RIDER (2026-07-10, after the DB schema chain): server_tasks.form_values (jsonb, currently always
NULL) was added in fix 3 exactly for this — store operator-supplied values there instead of
inventing new storage. Deployments are server_tasks rows; the injection point is the unified
worker. Sensitive prompted values follow the *Encrypted ciphertext convention — verify
DekRotationWalk covers encrypted members inside form_values and extend the walk if not.
```

### WP9 — Retention expansion

```text
TASK: Extend retention beyond deployment rows. Today RetentionService prunes deployments per
lifecycle-phase RetentionKeepDeployments (+ separate audit/AI-log retention jobs). Packages,
releases and runbook runs grow unbounded — disk exhaustion is a when, not an if.

Scope:
1. Release retention: Octopus semantics — a release is prunable when it no longer falls in any
   lifecycle phase's keep-window and has no retained deployments. Configure per lifecycle phase
   next to RetentionKeepDeployments (entity + migration + LifecycleDetail.razor editor).
2. Package retention: keep last N versions per package id (global default + per-package override
   optional — start with global, Configuration page setting). HARD CONSTRAINT: never delete a
   version referenced by a retained release's snapshot (ProcessSnapshot package pins) or by a
   retained deployment. Note: AgentPackageEntitlement scans deployment history — document that
   pruning deployments already revokes historical entitlement (known, accepted).
3. Runbook-run retention: keep last N runs per runbook (global default; runbook-level override
   optional later).
4. Mechanics: extend RetentionService; run via the existing Hangfire registration (per-account
   fan-out in multi-account — follow the registrar pattern). Add a DRY-RUN mode that logs what
   WOULD be deleted; first release ships with dry-run default ON via a feature flag
   (BuiltInFeatureCatalog) so operators can observe before enabling.
5. Audit summary entry per run (counts per category). File-store deletes go through the store
   abstractions (LocalPackageStore — account-scoped paths in multi-account).
6. Tests: reference-protection (retained snapshot pins survive), phase-window math, dry-run
   produces zero deletes.

Acceptance: with retention enabled on a seeded history, old packages/releases/runs are pruned,
nothing referenced survives-check fails, dry-run logs accurately, single-instance and
multi-account registrations both wired.

RIDER (2026-07-10, after the DB schema chain): this WP ABSORBS the log age-cap originally
scoped into schema fix 6: age-based pruning of task_log_entries for RETAINED executions, knob
in the retention/performance settings document (house rule 11) — RetentionService is extended
once, here. Runbook-run retention (item 3) operates on server_tasks kind=RunbookRun (children
cascade via the unified FKs). Item 2's global package-retention default is a settings-document
field, not a new table or column.
```

### WP10 — OpenTelemetry export

```text
TASK: Wire production telemetry export (M12 — the last whole milestone open). Instrumentation
exists; only a Console exporter runs in dev; production collects and DROPS all telemetry.

Scope:
1. Add OTLP export for traces + metrics (+ logs if the current pipeline routes them through OTel;
   check how Serilog and OTel interact in Program.cs before deciding — do not double-export logs).
   Package: OpenTelemetry.Exporter.OpenTelemetryProtocol via Directory.Packages.props.
2. Config-gated: Otel:Enabled + Otel:OtlpEndpoint (+ protocol grpc/httpProtobuf, optional headers
   for authenticated collectors). Disabled → exactly current behavior. Console exporter stays the
   Development default.
3. Resource attributes: service.name=krakendeploy-server, service.version from assembly, and
   deployment slot/node identity if blue-green identity is cheaply available (Router/slot docs).
   The Router (src/KrakenDeploy.Router) and Agent get the same treatment ONLY if they already
   carry OTel packages — check first; do not add new instrumentation surface in this WP.
4. Docs: add an "Observability" section to docs/on-prem-guide.md (endpoint config, example
   docker-compose collector snippet, what is exported). Mention data leaves the host — operators
   in regulated environments must point it at their own collector.
5. Seq pipeline (DECIDED 2026-07-06): additionally document AND smoke a Seq backend — logs (and
   traces if Seq's OTLP ingestion supports them in the current version — verify, don't assume)
   flowing from KrakenDeploy to a local Seq container (datalust/seq). Add a docker-compose
   snippet + config example to the on-prem guide Observability section.
6. Verify: boot with a local collector (docker: otel/opentelemetry-collector with logging
   exporter) and show spans/metrics arriving; repeat against Seq; boot with Otel disabled →
   no behavior change, no startup cost.

Acceptance: spans + metrics arrive at a local OTLP collector; logs visible in a local Seq;
disabled mode is a true no-op; README/on-prem-guide claims match reality.
```

### WP11 — Latent bug batch

```text
TASK: Close six small, unrelated latent defects. Independent items — commit separately.

1. Offline-drop Email delivery (DeploymentWorker.cs ~860): the UI offers an Email delivery channel
   that logs "Email delivery not yet implemented" and silently degrades to manual download. SMTP
   infra now exists (M13.B: EmailImmediateTransport, SmtpConfig). Implement: send a notification
   with the drop-bundle DOWNLOAD LINK (+ manifest summary) — do NOT attach the bundle (size).
   No SMTP configured → fail the delivery visibly (deployment log + audit), not silently.
2. Agent auto-update (Agent/Services/AgentUpdateService.cs ~267): the swap moves only the single
   exe out of the staging directory. Copy the ENTIRE staged payload (all files) with a rollback
   path if the swap fails mid-way (stage → backup current → move new → on failure restore backup).
   Add a test with a multi-file staged layout.
3. Server-script log sequencing race (Server.Transport/ServerScriptStepRunner.cs ~246): unguarded
   deployment.NextLogSequence++ per line in a fresh scope — two parallel server steps can take the
   same sequence. Route through the same LogSequencer/locking the agent path uses (find how
   AgentHub.AppendLogAsync allocates sequences and reuse that mechanism).
4. IIS auth toggles for webApplication/virtualDirectory shapes
   (steps/KrakenDeploy.Steps.KrakenIis/OctopusIisConfig.cs — MapWebApplication ~218,
   MapVirtualDirectory ~287): auth settings are parsed for webSite (:166-199 works, generator
   emits at :402-411) but IGNORED for the other two shapes. Map them and emit app/vdir-level
   auth configuration in IisScriptGenerator; extend KrakenIis tests for both shapes.
5. security.show-error-stack-traces (BuiltInFeatureCatalog.cs:34) has no consumer. Wire it into
   the error surface (Pages/Error.razor / exception handling middleware): flag ON → stack trace
   visible to authorized users; OFF (default) → generic message. If wiring is disproportionate,
   REMOVE the toggle — no inert knobs.
6. Old-chrome pages: bring root VariableSets.razor, Runbooks.razor, Tenants.razor and detail pages
   LifecycleDetail/VariableSetDetail/RunbookDetail/TenantDetail to the page-head/page-title
   pattern used by Lifecycles.razor (commit c6cb01c is the reference restyle).

Acceptance: each item verified individually (unit test or manual smoke as appropriate); no
regressions in Steps.KrakenIis and Agent test suites.

RIDER (2026-07-10, after the DB schema chain): item 3 (log-sequencing race) was folded into
schema fix 3 (unified log allocation) — verify ServerScriptStepRunner routes through the shared
sequencer and close the item with a regression test only; if fix 3 missed it, fix it here
against the unified task_log_entries.
```

### WP12 — Per-account DEK: unblock multi-account boot

```text
TASK: Multi-account mode currently CANNOT BOOT: Program.cs (RunWebAsync, ~263-270) fail-fasts
when MultiAccount:Enabled because envelope encryption's DekProvider is single-instance-only, and
EncryptionCommands.cs (~323) refuses rotation in multi-account. Implement per-account DEKs and
remove both fail-fasts. This is the last blocker for the SaaS deployment mode.

Step 1 — read the existing design + implementation first: docs/saas-multi-account-architecture.md,
the envelope encryption implementation (DekProvider, DekRotationWalk, AddDataEncryptionKeys
migration — note WHICH database the data_encryption_keys table lives in: catalog or tenant DB;
the fleet migrator applies tenant migrations per account). Write a 1-page addendum
(docs/design-per-account-dek.md, Draft) before coding.

Scope:
1. Account-aware DekProvider: cache unwrapped DEKs keyed by accountId (ConcurrentDictionary;
   Guid.Empty = single-instance). Each account's DEK row lives in that account's tenant DB
   (created at provisioning — extend AccountProvisioner/TenantInitializer so new accounts get a
   DEK generated + wrapped by the platform KEK at provision time). Existing accounts: a fleet
   backfill command (CLI) that generates missing DEKs.
2. The platform KEK stays config-level (one KEK, many DEKs) — per-account KEK is out of scope.
3. CLI: encryption rotate-dek gains --account <subdomain> (CliHost --account pattern is
   established: apikeys/backup/restore commands) and --all-accounts; rotate-kek re-wraps every
   account's DEK. Remove the EncryptionCommands.cs multi-account refusal.
4. Remove the RunWebAsync fail-fast. Boot in multi-account must fail-CLOSED per account if a DEK
   is missing/unwrappable (that account 503s; others unaffected) — never fall back to plaintext.
5. P3-5 while you're here: convert the five Scoped-in-multi-account cache services
   (FeatureFlagService, DeploymentFreezeService, MaintenanceModeService,
   PerformanceSettingsService — ServiceCollectionExtensions.cs:229-235 — and
   LicenseUsageCounter — Program.cs ~435) to account-keyed singletons mirroring
   PerAccountOidcProviderCache (key "x:{accountId}", Guid.Empty single-instance). Keep the DI
   registrations valid in BOTH modes; Dev host validates scopes at boot.
6. Verification: full solution build; encryption test suite; single-instance boot unchanged;
   docker-compose.smoke-multiaccount.yml green WITH envelope encryption enabled (extend the smoke
   script to assert a sensitive value round-trips per account and that acme's DEK cannot decrypt
   globex data — negative test); rotate-dek --account smoke on one account.

Acceptance: multi-account boots with encryption enabled; per-account rotation works offline;
cross-account decryption is impossible by construction; single-instance path byte-identical in
behavior.

RIDER (2026-07-10, after the DB schema chain): P3-5 changed shape. FeatureFlagService,
MaintenanceModeService and PerformanceSettingsService no longer exist as table-backed Scoped
caches — they read documents via SettingsService (fix 7). Account-key SettingsService's cache
plus the two survivors (DeploymentFreezeService, LicenseUsageCounter). The per-account DEK walk
must also cover the generic settings-document rotation step added in fix 7 (typed payloads with
*Encrypted members, per account DB).
```

### WP13 — Account & security feature batch

```text
TASK: Four security/account features, independent — commit separately.

1. User invites (M13.C.2): today "invite" = admin sets a temp password (InviteUserDialog +
   UserService.InviteAsync). Implement code-based invites: UserInvite aggregate (single-use code,
   expiry, optional pre-assigned teams, invited-by), admin UI on Configuration/Users.razor
   (create + revoke + resend), public registration page /register/{code} (anonymous route — check
   how Login.razor opts out of auth; page sets password, creates the user, applies team
   assignments, consumes the code), invite email via the existing SMTP transport when configured
   (otherwise show a copyable link). Audit: invited/registered/revoked. Guards: expired/consumed
   codes fail closed; rate-limit the public endpoint (an endpoint-scoped fixed-window limiter
   exists in Program.cs ~215 — reuse the mechanism).
2. Signing Keys UI (M13.D.1): step-package trust + adhoc signing currently use static config keys
   (StepPackages:TrustedPublicKey, Adhoc:SigningKey). Create a SigningKey entity (purpose enum:
   StepPackageTrust / AdhocSigning; public key material; status Active/Revoked; created/revoked
   timestamps), a Configuration page (list/add/revoke — no private-key display after creation),
   and make the verification paths read DB keys with config keys as legacy fallback (log a
   deprecation warning when the fallback is used). Migration path documented in the page help text.
3. AiCostOverride (M11.A.5.2): per-Space override of the embedded AiCostCatalog rates.
   Entity (ISpaceScoped) + DbBackedAiCostCatalog decorator over the static catalog + editor
   section on Pages/Ai/AiSettings.razor + audit on change. Budget-cap logic must pick up
   overridden rates.
4. UiHub deployment:{id} group hygiene: pushes to that group exist (AgentHub.cs:266,299,368,409;
   ServerScriptStepRunner.cs:266; DeployReleaseStepRunner.cs:451) but NO join path — the only
   AddToGroupAsync is the account group (UiHub.cs:45). First investigate how DeploymentDetail's
   live log actually reaches the browser today. Then either (a) delete the dead group pushes, or
   (b) implement an authorized join: hub method JoinDeployment(deploymentId) that verifies the
   caller's account AND Space-level permission to view that deployment before AddToGroupAsync.
   Do NOT leave pushes without an authorization story — a future join without checks is an
   instant cross-tenant log-stream leak.

Acceptance: invite round-trip works end-to-end (create → register → team applied → code dead);
signing-key rotation possible without config edits; AI rates overridable per Space and the
budget cap respects them; deployment group either gone or join-authorized with a test.

RIDER (2026-07-10, after the DB schema chain): item 3 — prefer extending the Space 'ai'
settings document (fix 7) with the cost-override rates instead of a new ISpaceScoped entity;
fall back to an entity only if per-rate rows genuinely need independent audit/concurrency.
Item 4 — AgentHub was rewritten in fix 3; re-locate the deployment:{id} (now task-scoped) group
pushes before deciding delete-vs-authorized-join.
```

### WP14 — Documentation reconciliation

```text
TASK: Make the paperwork match the shipped product. No code changes except the one dead link.

1. README.md rewrite (worst offender): current status (M1-M15 + blue-green + multi-account
   shipped, ~600+ tests), .NET 10 / SDK 10.0.300, real repo layout (all ~15 src projects + steps/
   + examples/ + templates/ + ~15 test projects), transport reality (SignalR + OfflineDrop; the
   "pluggable direct/polling" claim is stale), offline-drop channels as actually implemented.
   DECIDED (D8/D9, 2026-07-06): REMOVE the tsvector-search claim entirely (descoped); the
   Kraken PowerShell module stays on the roadmap — README lists it as "planned", clearly not
   shipped. Mirror both decisions in the TASKS.md locked-decisions section.
2. TASKS.md:
   a. Tick the stale-unchecked M10 narrative items superseded by the slice tracker (lines ~473-682)
      with a one-line annotation where done-differently (e.g. SpaceMembership → Teams+RoleAssignments).
   b. Fix the M10.1 slice-5 HA text: PostgresAgentConnectionRegistry + UNLOGGED table were REMOVED
      (migration 20260630122029); HA = in-memory registry + sticky sessions per docs/ha-pair.md.
   c. Mark M8 delivery-channel scope honestly (Email pending WP11, SFTP = file-share copy).
   d. Verify the M16 "Finish line" milestone section (created at plan kickoff, one checkbox per
      WP in docs/finish-plan-2026-07-05.md §2) is present and its ticks match merged reality.
   e. Add post-M15 milestone entries (one line each + key commits): blue-green slot deploy +
      Router, SaaS multi-account phases 1-3 + per-account SSO, Space-in-URL + Space isolation
      hardening, target↔tenant M2M redesign, projects dashboard + analytics pivot, offline runner,
      deployment failure modes, per-user API keys (M13.C.4), envelope encryption (M13.D.2),
      agent-transport account awareness. Source: git log + docs/*.md.
3. docs/db-erd.md: regenerate/refresh for the ~20 migrations since v1.0 (ApiKeys,
   DataEncryptionKeys, PivotViews, ProjectDashboardViews, DropAgentConnectionsTable, failure
   mode…); bump version + history table.
4. docs/mcp.md: add the 2 undocumented tools (run_adhoc_action, get_adhoc_session); bump version.
5. docs/self-upgrade-ha.md: mark Archived with a pointer to blue-green-slot-deployment.md.
6. docs/on-prem-guide.md: add the standard doc header (version/date/authors/status).
7. Write docs/ai-integration.md (the AiSettings page links to it): data-flow diagram (what leaves
   the server: sanitized prompts via PromptSanitizer, which providers), storage (AiCallLog
   retention), GDPR posture (processor location per provider, no production payloads in prompts,
   operator responsibilities), budget caps + two-person adhoc approval. Audience: operators in
   regulated environments. Then fix AiSettings.razor:27 to a working URL (repo blob URL).
8. Bump statuses: step-packages.md + sdk-surface.md (shipped → Approved w/ version bump),
   saas-multi-account-architecture.md + saas-phase3-account-awareness.md + offline-runner.md
   (implemented → note status), per the header convention.

Acceptance: a new developer reading README + TASKS.md gets an accurate picture; every doc has a
correct status header; the AI settings link resolves.

RIDER (2026-07-10): item 3 is CANCELLED — docs/db-erd.md was deleted as stale (2026-07-10); do
NOT recreate it unless explicitly asked. Extend item 2e: the DB schema hardening chain
(docs/db-schema-fix-prompts-2026-07-10.md, fixes 1-7), the WP1/WP2 completions, and the merged
execution order (finish-plan v1.2 §2) belong in the M16/TASKS.md reconciliation.
```

### WP15 — Certificates library (full v1, pre-go-live)

```text
TASK: Central certificate management (Octopus parity, decision D3 2026-07-06: full scope,
pre-go-live). Today IIS HTTPS bindings take a raw thumbprint/certificateVariable and certs must
be pre-installed on targets by hand. Permissions CertificateView / CertificateExportPrivateKey
are already reserved in the Permission enum (TASKS.md:562) — use them.

Step 1 — short design doc (docs/design-certificates.md, status Draft, ~2 pages): entity shape,
how cert material rides the wire to agents, IIS step integration, replacement/versioning. Then
implement.

Scope:
1. Certificate entity (ISpaceScoped): name, uploaded PFX/PEM (private key material encrypted at
   rest via the envelope-encryption pipeline like other sensitive columns), parsed metadata
   (subject, thumbprint, SANs, NotBefore/NotAfter, has-private-key), optional environment/tenant
   scoping, notes. Private key material is NEVER rendered after upload; export of the private
   key is gated by CertificateExportPrivateKey + audited.
2. Library UI (new page under the Library nav section): expiry-sorted list with expiring-soon
   badges, upload dialog (PFX password handled as sensitive), detail card, archive + replace
   flow — replacement preserves the reference identity (steps referencing the certificate pick
   up the new version without re-editing, Octopus-style version chain).
3. IIS integration: the KrakenIis step's https binding can reference a library certificate as an
   alternative to raw thumbprint. Study how certificateVariable flows today in
   steps/KrakenDeploy.Steps.KrakenIis (OctopusIisConfig + IisScriptGenerator) and resolve the
   thumbprint at deploy time from the referenced certificate.
4. Certificate-typed variable support so #{MyCert.Thumbprint}-style expansion works in scripts.
   Decide and document the exposed properties (Thumbprint, Subject, NotAfter at minimum).
5. Install-to-target step: a new step package (follow an existing steps/ project layout) that
   imports the certificate (PFX + password) into a chosen Windows store (default
   LocalMachine\My) on targets, idempotent by thumbprint. Certificate material must travel the
   same protected path as sensitive variables — verify what the agent wire actually exposes and
   ensure material never lands in logs, step outputs, or drop bundles in plaintext.
6. Expiry notifications: a Hangfire job (per-account fan-out pattern) emits a
   subscription-visible event when a certificate is within N days of expiry (default 30,
   configurable) so M13.B email/webhook subscriptions can alert.
7. Audit: upload / replace / archive / export / install-step usage.
8. Tests: PFX + PEM metadata parsing, IIS binding resolution from a reference, install-step
   idempotence, expiry event emission, export permission gate, sensitive-material never in logs.

Acceptance: upload a cert → reference it in an IIS https binding → deploy lands the binding with
the right thumbprint; the install step places the cert in the target store idempotently; an
expiring cert raises a subscription event; private key never appears in UI/logs/bundles;
permission gates enforced.
```

---

## 6. References

- Audit basis: five agent reports, 2026-07-05, session-internal; key prior docs: `docs/audit-2026-06-16.md`, TASKS.md, `docs/architecture.md`, `docs/saas-multi-account-architecture.md`, `docs/blue-green-slot-deployment.md`.
- Octopus Deploy feature reference: https://octopus.com/docs (parity table in §1.1).

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-07-05 | Initial audit + plan + WP1–WP14 prompts |
| 1.1 | 2026-07-06 | Grill session: all decisions resolved (§3), execution order + go-live line locked, WP3 approval model specced, WP10 + Seq, WP14 D8/D9 + M16, new WP15 certificates prompt |
| 1.2 | 2026-07-10 | WP1+WP2 done; DB schema chain (db-schema-fix-prompts-2026-07-10.md) inserted before WP3, merged order locked; preamble rules 4/5 updated + new rule 11 (SettingsService); RIDERs added to WP3-5, WP7-9, WP11-14; WP14 db-erd.md item cancelled (file deleted) |

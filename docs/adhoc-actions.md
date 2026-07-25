# KrakenDeploy — Ad-hoc Agent Actions

| | |
|---|---|
| **Status** | Approved |
| **Version** | 1.5 (M11.E + F2 + followups) |
| **Last updated** | 2026-07-25 |
| **Applies to** | KrakenDeploy server `/adhoc` page + `/mcp` `run_adhoc_action` tool, agent verify-then-run pipeline |
| **Technologies** | .NET 10, `System.Management.Automation` 7.6 (PowerShell AST parser), `RSA-SHA256` signing, SignalR control plane, Radzen Blazor UI |
| **Projects** | `KrakenDeploy.Contracts.Adhoc`, `KrakenDeploy.Server.Data.Services.Ai.Adhoc`, `KrakenDeploy.Server.Transport` (`AdhocDispatcher`, `AdhocSessionService`), `KrakenDeploy.Agent.Adhoc`, `KrakenDeploy.Mcp.Tools.AdhocTools` |

Ad-hoc agent actions let an operator type a natural-language request
("check disk free on the web tier", "restart the stuck w3svc"), have the
server's LLM **propose** a PowerShell script, vet it against a static-
analysis gate, **approve** it in the UI, and run it on a frozen set of
targets — with the LLM allowed to **propose one fix per iteration** when
the first attempt only partially succeeds.

The feature runs LLM-generated code on (potentially production) targets.
The whole point of the design is to make that safe. This document describes
what's guaranteed, what isn't, and where the seams sit.

---

## 1. Architecture: B2 (script-handoff)

```
operator prompt
      ↓
  AdhocSessionService.CreateSessionAsync     (Server.Transport)
      ↓                                            ↑ frozen target set
      ↓                                            ↑ + mode + iter cap
  AdhocGenerationService.GenerateAsync       (Server.Data, LLM call #1)
      ↓
  AdhocScriptGate.Analyze    ← M11.E.3, fail-closed (PowerShell SDK AST)
      ↓ (pass)
  AdhocIteration row, Status = PendingApproval
      ↓
  /adhoc/{id}   ← operator reviews + (optionally edits) + approves
      ↓
  AdhocScriptSigner.Sign  ← Adhoc:SigningKey (RSA private)
      ↓ signature bound to (SessionId, IterNumber, ScriptBytes)
  AdhocDispatcher.DispatchAsync   (Server.Transport)
      ↓ fan out per target in FrozenTargetSetJson
  IAgentHubClient.RunAdhocScriptAsync   (SignalR)
      ↓
  agent: AdhocScriptExecutor.HandleAsync
      ↓ load Adhoc:TrustedPublicKey
      ↓ AdhocScriptSigner.Verify           ← FAIL-CLOSED on mismatch
      ↓ (valid)
      ↓ MachineExecutionGate               ← F2: queue behind this box's running
      ↓                                      task. Skipped when the target sets
      ↓                                      AllowParallelTaskExecution.
      ↓ ScriptRunner.RunAndReturnExitCodeAsync (pwsh, captured stdout/stderr)
                                           ← ONE Adhoc:MaxTotalDuration budget
                                             spans the gate wait AND the run:
                                             REFUSE if it expires queued, kill
                                             the process tree if it expires
                                             running.
      ↓
  IAgentHubServer.ReportAdhocResultAsync   (SignalR)
      ↓
  PendingAdhocRegistry resolves the TCS; AdhocDispatcher collates per target
      ↓
  AdhocSessionService persists ResultsJson, iter Status = Completed
      ↓
  AdhocVerdictService.EvaluateAsync (LLM call #2 — per-target results)
      ↓ {AllSucceeded, NoFixAvailable, ProposeFix}
      ↓
  AdhocSessionService advances:
      - AllSucceeded / NoFixAvailable → Status = Closed
      - ProposeFix + cap reached      → Status = CapReached
      - ProposeFix + proposed-fix script fails gate → Status = Closed
                                                       + AdhocGateRejected audit
      - ProposeFix + passes gate      → iter N+1, Status = PendingApproval
                                          (operator approves again)
```

**Why B2 (script handoff), not B1 (LLM tool-calls into agents):** the
generated script is plain text, human-readable, **signed**, and gated by
operator approval at every turn. The LLM never holds a direct lever onto
the target; it can only *propose*. This breaks the prompt-injection-into-
auto-execution loop that B1 is vulnerable to.

---

## 2. Locked invariants (security contract)

These are the safety properties the implementation enforces structurally —
not "policies we hope to follow". Each row maps to where the invariant lives.

| Invariant | Locked because | Enforced in |
|---|---|---|
| **Frozen target set** — resolved once at session creation, immutable for life. | The LLM has no field that could expand the blast radius beyond what the operator picked. | `AdhocSession.FrozenTargetSetJson` (jsonb, write-once); `AdhocDispatcher.DispatchAsync` reads it as its only input — no API to pass a wider set. M11.E.15a / M11.E.17. |
| **Mode immutability** — a `Readonly` session can never run a `Mutating` script. | The operator's risk choice at session-creation time is final; an LLM-proposed fix can't quietly escalate. | `AdhocScriptGate.Analyze(_, AdhocMode.Readonly)` rejects anything not on the Get-/Test-/Measure-* allowlist (plus a curated safe-utility set). Re-runs on every iteration. M11.E.15b. |
| **Gate on every iteration** — the operator-approved final form of every iteration's script goes through the AST gate. | An operator might edit the script before approving; a proposed-fix script is a fresh attack surface. | `AdhocSessionService.GenerateFirstIterationAsync` + `ApproveIterationAsync` + the proposed-fix branch in `AdvanceAfterVerdictAsync` — all call `AdhocScriptGate.Analyze`. M11.E.15c. |
| **Signing on every iteration** — every approval signs the *exact* script bytes; the agent re-verifies. | The agent never trusts a script payload, only the signature gate. Tampering anywhere on the path breaks verification. | `AdhocScriptSigner.Sign` (server), `AdhocScriptSigner.Verify` (agent's `AdhocScriptExecutor`). Canonical input includes a schema version + the (`SessionId`, `IterNumber`, script bytes) tuple — replays across sessions or turns fail. M11.E.6 / M11.E.15e. |
| **Approver permission gate** — `Permission.AdhocActionsExecute` + Space membership. Single-approver by default; **two-person** is a per-Space opt-in (M11.E.11). | Single-approver matches the deployment-execute model; high-risk Spaces can require dual control. | `[Authorize(Policy = "perm:AdhocActionsExecute")]` on `/adhoc` pages; `EnsureAuthorisedAsync` in `AdhocTools` for MCP. M11.E.5. |
| **Two-person approval (opt-in)** — when `SpaceAiSettings.AdhocTwoPersonApproval` is on AND the iteration is high-risk (Mutating session OR any Production-risk target in the frozen set), signing+dispatch require a SECOND, distinct approver. | Dual control for the highest-blast-radius actions, without forcing it on every Space. | `AdhocSessionService.ApproveIterationAsync`: first approval → `PendingSecondApproval` (no sign/dispatch); second must differ from the first approver AND the session creator. Risk = MAX over the frozen targets' current `RiskLevel` (deleted/unresolvable target ⇒ Production, fail-safe). M11.E.11. |
| **Iteration cap** — per-Space (default 5, bounded 1–20); auto-closes with `Status = CapReached` and a "manual intervention required" log entry. | Stops a runaway loop if the LLM keeps proposing broken scripts; SaaS-tunable so each Space bounds its own blast radius + AI spend. | `AdhocSession.MaxIterations` is frozen at session creation from the current Space's `SpaceAiSettings.AdhocMaxIterations` (fallback `Ai:Adhoc:MaxIterationsPerSession`, then 5). `AdhocSessionService.AdvanceAfterVerdictAsync` checks before opening iter N+1. M11.E.14. |
| **Agent fail-closed verification** — missing public key, malformed PEM, signature mismatch, dynamic command invocation → refuse, report `AgentError`, do NOT execute. | The gate is the script's first filter; the signature is the script's last. The agent is the last line of defence. | `AdhocScriptExecutor.HandleAsync` — three explicit fail-closed branches, each reports back so the dispatcher's TCS resolves. |

### What the gate explicitly does NOT guarantee

Honesty up front. The AST gate is one layer of defence, not the only one.

1. **Direct .NET API abuse via type literals — now blocked (both modes).**
   `[System.IO.File]::Delete($p)`, `[System.Net.WebClient]::new().DownloadString(…)`,
   `[System.Reflection.Assembly]::Load(…)`, `[scriptblock]::Create(…)`,
   `([wmiclass]'Win32_Process').Create(…)`, `New-Object System.Net.WebClient`
   are member-expressions over a `TypeExpressionAst`/`TypeConstraintAst` (or a
   `New-Object` string argument), not `CommandAst`. The gate now walks every
   type reference + `New-Object` type argument and rejects a curated blocklist
   of types/namespaces (file I/O, network egress, reflection/code-loading,
   process control, WMI/ADSI, registry, in-process code exec) plus the
   dangerous type accelerators. Readonly mode additionally rejects destructive
   instance/static method calls (`.Kill()`, `.Delete()`, `.Terminate()`).
   **Residual:** instance methods on a runtime-typed variable beyond that
   curated verb set (receiver type unknown to static analysis), and a fully
   obfuscated type name resolved at runtime from a string (the literal never
   appears in the AST). **Mitigation for the residual:** mandatory operator
   approval per iteration; signing; mode immutability; frozen target set.
2. **File / registry writes whose path is a runtime variable.** Static
   analysis can flag literal `HKLM:\…` paths; it can't flag
   `Set-ItemProperty -Path $userInput`. **Mitigation:** same defence-in-
   depth; the operator reads the *full script* in the approval dialog.
3. **Time-of-check vs time-of-use** between gate + execution. The script
   the gate analysed is the script that gets signed and runs — there is
   no edit window between them — so this isn't a TOCTOU gap in the usual
   sense. But if an operator approves a script that *depends on external
   state*, that state can change between approval and execution. Treat
   it like any other script.
4. **`AllowParallelTaskExecution` rides OUTSIDE the signature (F2).** The
   signature binds the (`SessionId`, `IterNumber`, script bytes) triple; the
   per-target concurrency flag is not part of it, so an attacker with write
   access to the transport could flip it. That is deliberate and bounded: the
   flag decides only whether the script waits for the machine's execution slot,
   so flipping it changes *interleaving*, never *what runs* or *whether the
   operator approved it*. The flag is an execution-serialization hint, not an
   authorization input, and putting it under the signature would mean re-signing
   per target for a property the operator sets on the target, not on the script.
   **Mitigation:** the transport is mutually authenticated (agent JWT, A8) over
   TLS; a flip requires already owning the channel.

---

## 3. Configuration

| Key | Where | Format | Notes |
|---|---|---|---|
| `Adhoc:SigningKey` | server `appsettings.json` / env / KeyVault | RSA private key, inline PEM **or** a path to a `.pem` file | Required to sign any approved iteration. Missing → `AdhocFeatureUnavailableException(SigningKeyMissing)` on first approval. **Separate from `StepPackages:SigningKey`** — rotation cadence + custody can differ. |
| `Adhoc:TrustedPublicKey` | agent `appsettings.json` | RSA public key, inline PEM **or** path | Required on each agent for ad-hoc execution. Missing → agent refuses + reports an `AgentError`. |
| `SpaceAiSettings.AdhocMaxIterations` | per-Space row in `space_ai_settings` (UI: **Configuration → AI Settings**) | integer 1–20 | **Primary** iteration-cap source (default 5). Frozen onto each session at creation, so edits only affect later sessions. Validated 1–20 at the API boundary. |
| `Ai:Adhoc:MaxIterationsPerSession` | server | positive integer | Deployment-wide **fallback** used only when a Space has no `space_ai_settings` row at all. Hard default 5 when unset. |
| `SpaceAiSettings.AdhocEnabled` | per-Space row in `space_ai_settings` (UI: **Configuration → AI Settings**) | bool | Off by default. Disabling hides the **New session** CTA and surfaces an "Ad-hoc actions are disabled for this Space" banner. |
| `SpaceAiSettings.AdhocTwoPersonApproval` | per-Space row in `space_ai_settings` (UI: **Configuration → AI Settings**) | bool | Off by default. When on, high-risk iterations (Mutating OR a Production target) require a second, distinct approver before sign+dispatch. |
| `DeploymentTarget.RiskLevel` | per-target column `deployment_targets.risk_level` (UI: **target detail page**, MachineEdit) | `{Development,Staging,Production}` | Operator-set; **default Production** (fail-safe, backfilled). Drives the louder approval banner + the two-person trigger (session risk = MAX over the frozen set). |
| `Adhoc:MaxTotalDuration` | agent `appsettings.json` | `TimeSpan` string (e.g. `00:05:00`) — a BARE NUMBER means DAYS | **F2.** ONE budget covering queue wait **plus** execution, measured from receipt of the command. On expiry while queued the agent REFUSES and reports an `AgentError`; on expiry while running it KILLS the process tree and reports the timeout. Default 5 min, matching the server's per-target wait (`AdhocDispatcher.DefaultTimeout`) — a single bound is what actually stops a script outliving the dispatcher's verdict, which separate wait/run bounds did not (queue 3:59 + run 5:00 still beat a 5 min dispatcher). Unparseable, non-positive, or above 24 h → warn + default. |
| `DeploymentTarget.AllowParallelTaskExecution` | per-target column `deployment_targets.allow_parallel_task_execution` (UI: **target detail → Settings**, MachineEdit) | bool | **F2.** Off by default. When off the script queues behind any deployment / runbook run on that machine; when on it bypasses the slot and may interleave with them. Stamped per target onto each dispatched command. |
| `KrakenAiFeature.Adhoc` budget bucket | implicit | — | Every LLM call (generation + verdict) attributes to this bucket via the M11.A wrapper; budget overflow → `BudgetExceeded` typed exception → 503 to the UI / MCP caller. |

### Key generation example (operator runbook)

```powershell
# On the secure provisioning workstation:
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 `
    -out adhoc-signing.pem
openssl rsa -in adhoc-signing.pem -pubout -out adhoc-trusted.pem

# Server: set Adhoc:SigningKey to the path of adhoc-signing.pem (or
# paste the PEM into a secret store). RESTRICT the private key file's
# ACL to the kraken service account.

# Each agent: copy adhoc-trusted.pem to a directory the agent service
# can read; set Adhoc:TrustedPublicKey to that path.

# Keep the private key SEPARATE from the StepPackages signing key.
```

---

## 4. RBAC

| Permission | Block | Granted to | Purpose |
|---|---|---|---|
| `AdhocActionsExecute` | `1900` | **No built-in role** — must be granted explicitly via a dedicated role assignment. (The `System Administrator` god-mode role implies it, like every permission.) | Required to create a session, generate iterations, approve / reject / stop / mark-resolved. The same permission applies to the UI and the MCP `run_adhoc_action` tool. |

**Danger-zone, explicit-grant only.** `AdhocActionsExecute` is deliberately
withheld from the `Space Manager` and `System Manager` built-in roles — the
same treatment `AdministerSystem` gets. Running AI-authored PowerShell on
live targets is the single highest-blast-radius capability in the product,
so it must not ride along with general Space or delegated administration. An
operator who needs it creates a custom role carrying only `AdhocActionsExecute`
(scoped to the relevant Space) and assigns it to a named team. The
`BuiltInRbacSeeder` re-syncs role permission sets on every startup, so this
exclusion is also enforced on upgrade — any deployment that previously
seeded the permission onto a built-in role has it revoked on next start.

Single-approver is the default. **Two-person approval (M11.E.11)** is a
per-Space opt-in (`SpaceAiSettings.AdhocTwoPersonApproval`): when on, an
iteration that is high-risk — a `Mutating` session OR a frozen target set whose
**maximum** `RiskLevel` is `Production` — needs a second, distinct approver
before the server signs + dispatches. The second approver must differ from the
first approver and from the session creator; the script can't be edited at
second approval (it would invalidate the first review). Effective risk is
recomputed at each approval from the targets' current classifications, so a
mid-session reclassification tightens the next approval. A since-deleted or
unresolvable target counts as `Production` (fail-safe).

---

## 5. UI (`/adhoc`)

- **Sessions list** (`/adhoc`) — newest-first; per-Space `AdhocEnabled`
  gate; "New session" CTA hidden when disabled or operator lacks
  `AdhocActionsExecute`.
- **Session detail** (`/adhoc/{Id}`) — prompt + frozen target set
  (immutable, display-only); one card per iteration with script
  (monospace), per-target results table, narrative, verdict badge;
  per-iteration **Approve / Edit and approve / Reject** dialog; session-
  header **Mark resolved** + **Stop session**.
- **Approval dialog** — risk banner (louder on `Mutating` + `RequiresMutation=true`),
  full script in a monospace block, **Edit and approve** swaps in a
  textarea whose contents are re-gated before signing. A gate rejection
  surfaces inline with the violation summary; signing key missing surfaces
  as "Ad-hoc signing key not configured — contact a server admin."

UI is **compile-verified only** in v1 — no Blazor runtime test harness
exists in this repo, consistent with the rest of the M11 milestone.

---

## 6. MCP tool surface

External AI clients (Claude Desktop, Cursor, Copilot Chat) can drive
ad-hoc actions via two tools on the existing `/mcp` endpoint:

### `run_adhoc_action`

Initiates a session. Takes `prompt`, `mode` (`readonly` / `mutating`),
`targetIds` (explicit GUIDs — the frozen set). Returns the proposed
script + risk + a deep-link URL.

**The MCP tool NEVER auto-approves.** The returned `AdhocInitiationResult`
carries `ApprovalPending = true` and a `HumanApprovalRequiredNote`
explaining that an operator must approve at the deep-link URL. This is
the "approval gate still enforced server-side regardless of the source"
requirement (M11.E.10) — the MCP entry point only initiates and reports;
the safety contract from §2 applies identically.

The same `AdhocActionsExecute` permission gate applies to the MCP
caller's API-key principal (`EnsureAuthorisedAsync` runs first).

### `get_adhoc_session`

Read-only fetch of the session state: status, per-iteration approval
state, per-target results, latest verdict. Useful for the MCP client to
poll "did the operator approve my proposed action yet?" after calling
`run_adhoc_action`.

---

## 7. Out of v1 scope (documented)

These are the high-risk patterns whose ABSENCE is intentional:

- **Autonomous remediation** — the LLM acting without operator approval
  at any turn. The whole design rejects this; iterated-B2 is the
  compromise.
- **Persistent cross-session agent memory** — each session is
  independent. An operator wanting context from a prior session passes
  it in the prompt.
- **Per-target divergent scripts within a single iteration** — one
  script per iteration runs on the full frozen set. If the operator
  needs different actions on different subsets, they start separate
  sessions with narrower target selectors.
**Future-work — revise the reflection/assembly-load block.** The current
dangerous-type blocklist rejects all of `System.Reflection` (and `Add-Type`),
which over-blocks legitimate reflection use (much functionality is only
reachable via `[Reflection.Assembly]::Load*`). Planned revision: switch from a
flat type block to an **argument-shape** rule that distinguishes the RCE
overload `Assembly.Load($bytes)`/`Load($stream)` (non-literal argument → block)
from a named/literal load `Assembly.Load('System.Web')`/`LoadWithPartialName`/
`LoadFrom('literal')` (`StringConstantExpressionAst` argument → allow), most
likely mutating-mode-only, leaning on the operator-approval + signing layers.
Same heuristic for `[Activator]::CreateInstance`. Loosens the gate, so gated
behind STOP-AND-ASK.

---

## 8. References

- `TASKS.md` → §M11.E sub-tasks 1 through 17.
- `docs/architecture.md` for the pre-production policy + the
  `StepPackageSigner` recipe that `AdhocScriptSigner` mirrors.
- `docs/mcp.md` for the MCP server enable + auth model that the
  `run_adhoc_action` tool reuses.
- `src/KrakenDeploy.Server.Data/Services/Ai/Adhoc/` — generation,
  verdict, signing key, the static-analysis gate.
- `src/KrakenDeploy.Server.Transport/AdhocDispatcher.cs` +
  `AdhocSessionService.cs` — the dispatch path and the state machine.
- `src/KrakenDeploy.Agent/Adhoc/AdhocScriptExecutor.cs` — the agent's
  fail-closed verify-then-run handler.
- `src/KrakenDeploy.Mcp/Tools/AdhocTools.cs` — the MCP entry point.

---

## 9. History

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-05-29 | Initial release (M11.E commits 1–7). |
| 1.4 | 2026-07-25 | **F2** — ad-hoc scripts now take the agent's machine execution slot instead of bypassing it (an approved diagnostic could previously run straight into a deployment's file / IIS / service operations). Per-target opt-out via `DeploymentTarget.AllowParallelTaskExecution`, stamped onto each command. CONTRACT CHANGE: `AdhocScriptCommand` gains `AllowParallelTaskExecution` (outside the signature binding — it is an execution-serialization hint, not an authorization input); `AgentContract.CurrentVersion` 1 → 2. |
| 1.5 | 2026-07-25 | **F2-followup 3** — the gate wait and the run share ONE `Adhoc:MaxTotalDuration` budget measured from receipt, replacing the queue-only `Adhoc:MaxQueueWait`. Separate bounds did not deliver the property they claimed: a 3:59 queue plus a 5:00 run still outlived the dispatcher's 5 min verdict, so a script could execute — and mutate a box — after the operator had been told it timed out. Expiry while queued REFUSES; expiry while running kills the process tree. |

# KrakenDeploy — Model Context Protocol (MCP) Server

| | |
|---|---|
| **Status** | Approved |
| **Version** | 1.1 (M11.B + M13.C.4 per-user keys) |
| **Last updated** | 2026-07-03 |
| **Applies to** | KrakenDeploy server `/mcp` endpoint + the `kraken-mcp` stdio proxy |
| **Technologies** | `ModelContextProtocol` 1.3.0 (C# SDK), Streamable HTTP transport, .NET 10 |

KrakenDeploy hosts an in-process **MCP server** so AI assistants (Claude
Desktop, Cursor, GitHub Copilot Chat, or anything that speaks MCP) can read
deployment state and run a small set of operator-authorised actions —
"why did last night's deploy fail?", "what changed since it last worked?",
"retry it". The server exposes **read-only resources** (addressable content)
and **tools** (RPC, including one mutating action), all behind the existing
API-key auth and a per-Space enable flag.

---

## 1. Enabling MCP

MCP is **off by default**. Two things gate it:

1. **Per-Space flag** — turn on *MCP* in **Configuration → AI Settings**
   (`SpaceAiSettings.McpEnabled`). With it off, every `/mcp` request gets
   `403` with a clear JSON body. Toggling it off is the kill switch — it
   doesn't remove the API key. For a **Space-restricted** key the gate reads
   the key's bound Space; unrestricted keys are gated on the Default Space.
2. **API key** — a **per-user** key (M13.C.4): mint one under
   **Configuration → API Keys** (or `apikeys create` on the server box) and
   send it in the `X-Api-Key` header. The `kraken-mcp` proxy injects it for
   you. The key authenticates AS its owning user — every permission check
   resolves the owner's real team/role grants, so an MCP client can never do
   more than the key's owner. Mint keys for a **service account** to give an
   MCP client its own least-privilege identity and audit trail.

> **Authorization notes.** `retry_deployment` (the one mutating tool)
> requires `Permission.DeploymentCreate` on the caller; the ad-hoc tools
> require `AdhocActionsExecute` as before. A Space-restricted key is denied
> everything outside its bound Space. Two deferred resources remain
> unimplemented: `deployments/{id}/artifacts/{name}` and
> `step-packages/{name}/{version}/manifest` (no tool has needed them).
> Every resource read + tool call still writes an `Mcp.*` audit row.

---

## 2. Connecting a client

Most stdio MCP clients launch a local command and talk to it over
stdin/stdout. `kraken-mcp` is that command: it bridges the client's stdio
to the remote server's `/mcp` HTTP endpoint and injects the API key.

### Claude Desktop

`claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "kraken": {
      "command": "kraken-mcp",
      "args": ["--server", "https://kraken.du.laus.hr"],
      "env": { "KRAKEN_API_KEY": "kdeploy-..." }
    }
  }
}
```

### Cursor / Copilot Chat

Same shape — point the client's MCP server config at the `kraken-mcp`
command with the `--server` arg and `KRAKEN_API_KEY` in the environment.

> Prefer `KRAKEN_API_KEY` (env) over `--key <key>` (arg): the env var keeps
> the key out of the client's config file diff + the process table.

### Building `kraken-mcp`

Self-contained single-file binary, one per platform:

```powershell
dotnet publish src/KrakenDeploy.Mcp.Cli -r win-x64    -c Release
dotnet publish src/KrakenDeploy.Mcp.Cli -r linux-x64  -c Release
dotnet publish src/KrakenDeploy.Mcp.Cli -r osx-arm64  -c Release
```

The output is a dependency-free `kraken-mcp` executable — drop it on the
operator's workstation `PATH` (or reference it by absolute path in the
client config).

---

## 3. Resources (read-only)

| URI template | Returns |
|---|---|
| `kraken://projects/{slug}/process` | Live deployment process — slim per-step list (curated config summary + `fullConfigUri` per step). |
| `kraken://releases/{slug}/{version}/process` | Frozen process snapshot for a release. |
| `kraken://projects/{slug}/process/steps/{index}/config` | **Full** unredacted config dict for one live step (drill-down). |
| `kraken://releases/{slug}/{version}/steps/{index}/config` | Full config dict for one snapshot step. |
| `kraken://deployments/{id}/log` | Complete deployment log as newline-delimited JSON. |
| `kraken://targets/{name}/health` | Target status, heartbeat, agent info, roles, last deploy result. |
| `kraken://releases/{slug}/{version}` | Release manifest (version, channel, step count, …). |

**Drill-down model:** the process resources keep each step lean — a curated
3-5 key summary plus a `fullConfigUri`. When the AI needs the complete
(possibly gnarly) config to diagnose a step, it reads that
`.../steps/{index}/config` resource. Curation never loses data; it defers
the bulk to an explicit second read.

---

## 4. Tools

| Tool | Args | Action |
|---|---|---|
| `list_failed_deployments` | `environmentName?`, `projectSlug?`, `sinceHours?` | Recent failed/warning deployments, newest first. |
| `get_deployment_log` | `deploymentId`, `tailLines?` | Summary + log tail (full log via the log resource). |
| `get_deployment_diff` | `deploymentId` | Release / package / variable-name / target deltas vs last green run. |
| `get_step_config` | `deploymentId`, `stepIndex` | Full config dict for one snapshot step. |
| `get_target_health` | `targetName` | Target health snapshot. |
| `query_targets` | `role?`, `environmentName?` | Slim target listing, filtered. |
| `get_release_history` | `projectSlug`, `count?` | Release manifests, newest first. |
| `retry_deployment` | `deploymentId` | **Mutating** — creates a NEW deployment of the same release + environment + target set; returns the new id. |

`get_deployment_diff` is the fastest path to a regression's cause: it diffs
a deployment against the last *Succeeded* run of the same project +
environment and reports what changed. Variable **names** are reported, never
values — a changed value could be a secret.

---

## 5. Security posture

- **No agent egress.** AI calls never originate from deployment-target
  agents (production nodes in segmented AD networks have no outbound path).
  MCP traffic terminates at the server, which already has the egress it
  needs. The AI client connects to the server; the server talks to nothing
  external on the MCP path.
- **Audit trail.** Every resource read writes `Mcp.ResourceRead`; every tool
  call writes `Mcp.ToolInvoked` (subject = resource URI / tool name). The
  mutating `retry_deployment` *also* writes the created deployment's own
  domain audit. A forensic review of "what did the AI touch?" reads the
  `Mcp.*` events.
- **Sensitive-value protection.** The curated config summaries + the diff's
  variable section surface key **names**, not values. Full config values are
  reachable only through the explicit, audited `.../config` drill-down — the
  same read an operator would do in the UI.
- **Per-Space kill switch.** Flipping *MCP* off in AI Settings stops all
  `/mcp` traffic immediately (30 s cache) without disturbing the API key or
  other surfaces that share it.

---

## 6. Troubleshooting

| Symptom | Likely cause |
|---|---|
| `403 — MCP server is disabled for this Space` | The per-Space *MCP* flag is off (for a restricted key: in the key's bound Space). Enable it in Configuration → AI Settings. |
| `401` on connect | Missing/wrong/revoked/expired `X-Api-Key`. Check `KRAKEN_API_KEY` / `--key` carries a live per-user key (Configuration → API Keys); the server log names the precise reason. |
| `kraken-mcp: --server must be an absolute http(s) URL` | Pass a full URL, e.g. `https://kraken.du.laus.hr` (no trailing `/mcp` — the proxy appends it). |
| Client shows no tools | Confirm the client launched `kraken-mcp` (check its MCP logs) and the server build includes the MCP endpoint (`/mcp` mapped). |
| Tool returns "No deployment found" | The id/slug doesn't exist in the Space the request resolves to — a Space-restricted key sees only its bound Space; an unrestricted key resolves to the Default Space for Space-scoped lookups. |
| `403 — Caller does not have Permission.DeploymentCreate` (retry) / `AdhocActionsExecute` (adhoc) | The key's owning account lacks the permission in any Space it can reach. Grant it (a per-Space grant is enough for an unrestricted key; a restricted key needs it in its bound Space). |

---

## References

- MCP specification — <https://modelcontextprotocol.io/specification/>
- C# SDK — <https://github.com/modelcontextprotocol/csharp-sdk>
- KrakenDeploy AI integration overview — `docs/ai-integration.md`

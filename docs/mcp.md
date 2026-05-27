# KrakenDeploy — Model Context Protocol (MCP) Server

| | |
|---|---|
| **Status** | Approved |
| **Version** | 1.0 (M11.B) |
| **Last updated** | 2026-05-27 |
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
   doesn't remove the API key.
2. **API key** — MCP reuses the server's existing API-key scheme. Set
   `ApiKey:Key` in the server config; clients send it in the `X-Api-Key`
   header. The `kraken-mcp` proxy injects it for you.

> **v1 single-key caveat.** Today the API key is a single shared
> CLI-style credential with no per-user permission scoping, so an MCP
> client effectively has the same access the `kraken` CLI does, scoped to
> the **Default Space**. Granular per-user keys with Space binding + a real
> `DeploymentExecute` gate on `retry_deployment` arrive with M13.C.4. Until
> then, treat an MCP API key as a full-access credential and rely on the
> audit trail (every resource read + tool call writes an `Mcp.*` audit row).

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
| `403 — MCP server is disabled for this Space` | The per-Space *MCP* flag is off. Enable it in Configuration → AI Settings. |
| `401` on connect | Missing/wrong `X-Api-Key`. Check `KRAKEN_API_KEY` / `--key` matches the server's `ApiKey:Key`. |
| `kraken-mcp: --server must be an absolute http(s) URL` | Pass a full URL, e.g. `https://kraken.du.laus.hr` (no trailing `/mcp` — the proxy appends it). |
| Client shows no tools | Confirm the client launched `kraken-mcp` (check its MCP logs) and the server build includes the MCP endpoint (`/mcp` mapped). |
| Tool returns "No deployment found" | The id/slug doesn't exist in the **Default Space** (v1 scopes to Default). |

---

## References

- MCP specification — <https://modelcontextprotocol.io/specification/>
- C# SDK — <https://github.com/modelcontextprotocol/csharp-sdk>
- KrakenDeploy AI integration overview — `docs/ai-integration.md`

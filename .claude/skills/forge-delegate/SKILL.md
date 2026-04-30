---
name: forge-delegate
description: >
  Offload well-scoped work to the `forge` CLI instead of spawning Claude
  Agent({...}) subagents. USE WHEN: "delegate to forge", "offload to forge",
  "have forge implement", "use forge agent", "run forge", "forge it", or when
  a task is long, well-scoped against a forge agent's input_schema, and cost /
  isolation matters more than tight live-feedback integration. NOT for short
  tasks, parallel multi-agent reasoning in one turn, or tasks needing live
  mid-flight tool feedback.
---

# forge-delegate

Wraps the `forge` CLI for delegating tasks to forge agents. Two modes:
**sync** (block on result, single Bash call) and **background** (kick off,
get a handle, poll, collect later).

Assumes `forge` is on `PATH`. Run `forge --version` to confirm before first
use.

## When to use forge vs Claude `Agent({...})`

| Situation | Choice |
|---|---|
| Task has well-scoped JSON input matching a forge agent's `input_schema` | **forge** |
| Task is long-running (>~2 min) and bash/Docker startup amortizes | **forge** |
| Cost matters; want to run on a self-hosted LiteLLM endpoint | **forge** |
| Need parallel reasoning across results in one Claude turn | **Claude `Agent`** |
| Short task, or live mid-flight tool feedback matters | **Claude `Agent`** |
| No matching entry in `forge list agents` | **Claude `Agent`** (or write a forge agent first) |

## Discovery — what agents are available?

```bash
forge list agents
```

Returns JSON: `{"agents":[{"name":"...","root":"..."}]}`. Forge searches three
roots in order: `<cwd>/.forge/`, `~/.config/forge/`, `~/.forge/` — first match
wins. Use `forge describe agent <name>` to inspect `input_schema` and
`output_schema` before invoking.

## Sync invocation

Block until forge exits. Stdout = the agent's final JSON output. Stderr =
forge's logs. Exit code is propagated.

Bash:
```bash
bash ~/.claude/skills/forge-delegate/scripts/delegate-sync.sh <agent-name> '<input-json>'
```

PowerShell:
```powershell
& "$env:USERPROFILE\.claude\skills\forge-delegate\scripts\delegate-sync.ps1" <agent-name> '<input-json>'
```

Example (bash):
```bash
bash ~/.claude/skills/forge-delegate/scripts/delegate-sync.sh forge-dev \
  '{"task":"add --verbose flag to forge agent command"}'
```

## Background invocation

`delegate-bg` returns a handle path immediately (a directory under
`~/.forge/delegations/<uuid>/`). `delegate-status` consumes the handle and
emits JSON describing the run's state.

Bash:
```bash
HANDLE=$(bash ~/.claude/skills/forge-delegate/scripts/delegate-bg.sh forge-dev '{"task":"..."}')
bash ~/.claude/skills/forge-delegate/scripts/delegate-status.sh "$HANDLE"
```

PowerShell:
```powershell
$handle = & "$env:USERPROFILE\.claude\skills\forge-delegate\scripts\delegate-bg.ps1" forge-dev '{"task":"..."}'
& "$env:USERPROFILE\.claude\skills\forge-delegate\scripts\delegate-status.ps1" $handle
```

### Status output schema

```jsonc
{
  "state":   "running" | "completed" | "failed" | "rate_limited" | "caller_deferred" | "cancelled",
  "exitCode": <int>,                  // omitted while state=="running"
  "result":   <agent's JSON output>,  // present when state=="completed" AND stdout parses as JSON
  "runId":    "<agent>-<ts>-<rand>",  // best-effort recovery; may be omitted
  "stdout":   "<last 4 KB of stdout>",// present when state != "completed" (forge sometimes
                                      //   writes failure markup to stdout, contract-violating
                                      //   but observed for "agent not found" exit-1 path).
                                      //   Also present if state=="completed" but stdout was
                                      //   not parseable as JSON.
  "stderr":   "<last 4 KB of stderr>" // present when stderr.log is non-empty
}
```

| `state` | Forge exit | When |
|---|---|---|
| `running` | — | Process still alive. |
| `completed` | 0 | Success — `result` carries the agent's final JSON. |
| `failed` | 1 | Config / validation error (bad agent name, invalid input). |
| `failed` | 2 | Runtime failure (`AgentException`, `ProviderException`, etc.). |
| `rate_limited` | 6 | Resumable. Stderr JSON includes `retryAfterSeconds`. |
| `caller_deferred` | 7 | Resumable. Agent called `ask_caller`; resume with `forge resume <runId> --answer '<json>'`. |
| `cancelled` | 130 | Ctrl-C / SIGTERM. |

### Polling pattern

Inside Claude, poll **once per turn** rather than spinning a sleep loop —
the user is still typing. If the run is short, prefer sync mode.

```bash
# Single poll
STATUS=$(bash ~/.claude/skills/forge-delegate/scripts/delegate-status.sh "$HANDLE")
STATE=$(echo "$STATUS" | jq -r .state)
```

## Exit-code handling — caller's responsibility

The wrapper does **not** auto-resume. Decide per code:

| Code | Recommended action |
|---|---|
| `0` | Use `result` directly. |
| `1` | Inspect `stderr`; usually a bad agent name or invalid input shape. Fix and retry. |
| `2` | Inspect `stderr` for `errorKind`. Surface to user with diagnosis. |
| `6` | Read `retryAfterSeconds` from stderr JSON. Either wait + `forge resume <runId>`, or surface to user. |
| `7` | Agent is asking a question. Inspect `~/.forge/runs/<runId>/stages/agent/` for the question payload. Resume with `forge resume <runId> --answer '<json>'`. |
| `130` | User cancelled. Surface to user. |

## Run-id recovery (best-effort)

Forge generates the `run-id` inside the process and does not emit it before
completion. `delegate-status` recovers it post-hoc by scanning
`~/.forge/runs/<agent>-*` for directories with mtime ≥ the bg-mode
`started_at`.

**Limitation:** if two same-agent runs start within the same wall-clock
second, mis-attribution is possible. Accepted for v1. Mitigations: stagger
launches, or use sync mode when run-id certainty matters.

## Limitations

- **Single-conversation lifetime.** Background handles in
  `~/.forge/delegations/` aren't guaranteed to survive `/clear`. Re-issue if
  Claude session resets.
- **`nohup` constraint (bash).** Works on Linux / WSL / Git-Bash; dicier on
  bare MSYS2. Use the PowerShell sibling on native Windows shells.
- **No auto-install check.** Missing `forge` surfaces as a shell error
  (`forge: command not found`).
- **Heavyweight agents.** Agents using the `bash` tool (e.g. `forge-dev`)
  spin up Docker on first call (~5–10s). Only delegate work that amortizes
  the startup.
- **Shell-flavor consistency.** Don't `delegate-bg` from bash and
  `delegate-status` from PowerShell on the same handle (or vice versa).
  Metadata-file encodings are interoperable but not formally guaranteed.

## Source

`scripts/delegate-{sync,bg,status}.{sh,ps1}` are bash + PowerShell siblings
producing identical JSON shapes. Read them under `scripts/` if you need to
tweak invocation.

---
layer: Domain
title: Tools
updated: 2026-04-29
code_refs: [src/Forge.Tools/, src/Forge.Tools/ITool.cs, src/Forge.Tools/ToolBase.cs, src/Forge.Tools/ToolRegistry.cs, src/Forge.Tools/BuiltInToolsRegistration.cs, src/Forge.Tools/BashTool.cs, src/Forge.Tools/Docker/MountComposer.cs, src/Forge.Core/Config/BashConfig.cs]
related: [Application/AgentRunner.md, Domain/agents.md]
---

# Tools

A tool is a typed, JSON-Schema-validated operation the agent's tool loop can call. `ITool` is the contract; `ToolBase<TIn, TOut>` derives the schemas from CLR types via `SchemaExporter` and validates I/O on every call.

## Contract

```csharp
public interface ITool
{
  string Name { get; }
  string Description { get; }
  JsonSchema InputSchema { get; }
  JsonSchema OutputSchema { get; }
  Task<JsonNode> ExecuteAsync(JsonNode input, ToolContext ctx, CancellationToken ct);
}
```

`ToolBase<TIn, TOut>.ExecuteAsync` validates the `JsonNode` input against `InputSchema`, deserialises to `TIn` (camelCase, `JsonSerializationDefaults.CamelCaseTool`), calls `ExecuteCoreAsync`, then serialises `TOut` back to `JsonNode`.

## Registration

`ToolRegistry` holds the set of tools available to a run. `BuiltInToolsRegistration.AddForgeBuiltInTools(IServiceCollection)` wires the seven stock tools into DI plus a singleton `ToolRegistry`.

## Built-in tools

Forge ships exactly seven tools. There are no filesystem read/write/glob/grep/patch tools — agents that need filesystem work invoke `bash` against an explicitly-mounted host path.

| Tool | Class | Purpose |
|---|---|---|
| `fetch_url` | `FetchUrlTool` | HTTP GET; returns status, content-type, body text. 30 s timeout. |
| `web_search` | `WebSearchTool` | External search. Provider selected by `FORGE_SEARCH_PROVIDER` (only `brave` in v1) plus `FORGE_SEARCH_BRAVE_KEY`. |
| `llm_complete` | `LlmCompleteTool` | One-shot LiteLLM completion via `ILlmClient`. No tool loop. Falls back to `LiteLlmConfig.DefaultModel` when input omits `model`. |
| `ask_caller` | `AskCallerTool` | Pause the loop and ask the caller a question via `ICallerIo`. Headless callers may auto-deny / fail-fast / defer. |
| `notify_caller` | `NotifyCallerTool` | One-way status update to the caller; never blocks. |
| `request_approval` | `RequestApprovalTool` | Ask the caller to approve a named action. Caller policy decides headless behaviour. |
| `bash` | `BashTool` | Run a single command via `docker exec` inside a per-run container. Opt-in (requires a `bash:` config block on the agent). |

## Synthetic `submit_final`

`AgentRunner` injects a tool named `submit_final` whose `InputSchema` IS the agent's `output_schema`. Calling it terminates the loop and returns its argument as the agent's result. It is not in any registry; do not shadow the name.

NEVER reuse the name `submit_final` for any registry tool — the agent loop short-circuits on it before the registry is consulted.

## `bash` tool

The `bash` tool runs a single command inside a pre-started, per-run Docker container. It is **not** in the default registry — an agent must explicitly list `bash` in its `tools:` and provide a `bash:` config block (parsed into `BashConfig`). Any agent that declares `bash` in `tools:` but omits the `bash:` block fails at parse time.

### Input / output

```jsonc
// BashInput
{
  "command": "printf hi > /repo/greeting.txt",  // required, bash -lc
  "cwd": "/repo",                                // optional, must be /run or under a configured mount
  "env": { "LANG": "C.UTF-8" },                  // optional, keys must appear in env_allow
  "timeoutSec": 30                                // optional per-call override, clamped to [1, config.timeout_sec]
}

// BashOutput
{
  "exitCode": 0,
  "stdout": "…",                                  // capped at StreamCap.DefaultStdoutCapBytes; truncated: true when hit
  "stderr": "…",
  "truncated": false,
  "durationMs": 12,
  "diffs": [                                     // null when no rw mounts; empty when no changes
    { "path": "greeting.txt", "kind": "added", "hashBefore": null, "hashAfter": "…", "sizeDelta": 2 }
  ],
  "diffsPartial": false                          // true when the pre/post scan hit a traversal cap
}
```

The tool never raises on a non-zero exit code — agents inspect `BashOutput.ExitCode`. It does raise on wall-clock timeout and on stream hard-kill (output exceeded 64 MiB).

### Mounts (composed by `MountComposer.Compose`)

Mounts are explicit. The agent author declares every host path the container can see via `bash.mounts:` in the agent YAML. There is no `FsScope`-derived projection.

```yaml
bash:
  image: forge-bash@sha256:…
  mounts:
    - { host: C:/Repos/forge,        container: /repo,      mode: rw }
    - { host: C:/Repos/forge/tests,  container: /tests,     mode: ro }
```

| Field | Required | Notes |
|---|---|---|
| `host` | yes | Absolute host path. Symlinks resolved (≤ 8 hops). Windows extended-length (`\\?\…`) and UNC paths rejected. Path > 260 chars rejected on Windows. |
| `container` | yes | Absolute container path. Must start with `/`. `/run` is reserved for the per-run workspace. Each container path must be unique. |
| `mode` | yes | `ro` or `rw`. |

Forge always adds a `/run` (rw) mount pointing at the per-run workspace (`{forgeHome}/runs/<run-id>/`); user-declared mounts are layered on top.

`BashInput.cwd` is validated against the composed mount plan via `MountComposer.ResolveContainerCwd`:
- Must start with `/`.
- Must equal or be under one mount's container path. Otherwise: `cwd '<path>' does not map to any mount. Use '/run' or one of the configured 'bash.mounts' container paths.`
- The resulting suffix may not contain a `..` segment.
- When the call writes (everything but a strict probe), the matched mount must be `rw`.

`BashInput.cwd` defaults to the first user mount's container path, or `/run` when no user mounts are declared.

### Security posture (enforced by `DockerContainerLifecycle` + `DockerArgv.BuildRunArgs`)

| Docker flag | Default | Override? |
|---|---|---|
| `--network` | `none` | `bash.network: bridge` (opt-in only) |
| `--cap-drop` | `ALL` | — |
| `--security-opt` | `no-new-privileges` | — |
| `--user` | `1000:1000` | `bash.user:` (UID 0 rejected at parse time) |
| `--read-only` | off (set `bash.read_only_root: true` to flip) | rootfs writes blocked when on |
| `--pids-limit` + `--ulimit nproc=…` | `100` | `bash.pids_limit:` |
| `--memory` | `512m` | `bash.memory:` |
| `--cpus` | `1.0` | `bash.cpus:` |
| `--storage-opt` | empty (Linux overlay+xfs+pquota only) | `bash.storage_opt:` — daemons that reject the flag emit `bash_storage_opt_skipped` and retry without it |
| `--platform` | `linux/amd64` | `bash.platform:` (Apple Silicon) |
| Image ref | must contain `@sha256:` | parse-time reject otherwise |

`bash.env` is allowlist-gated: every key must appear in `bash.env_allow` AND must not match `BashConfig.ForbiddenEnvPattern` (`PATH`, `LD_*`, `DYLD_*`, `NODE_OPTIONS`, `PYTHONPATH`). The allowlist is empty by default.

`BashConfig.ForbiddenKeys` (`cap_add`, `privileged`, `pid_host`, `ipc_host`, `userns_host`, `devices`, `extra_mounts`) are rejected outright at parse time.

### Diff scan

Between the pre-exec and post-exec passes `MtimeHashScanner` walks every `rw` mount, capped by `BashConfig.Diff` (`max_files = 10_000`, `max_depth = 16`, `max_hash_bytes = 4 MiB`). Resulting `diffs` are surfaced on the tool output and recorded on the `AgentWriteLedger` with `ToolName = "bash"` and `RootCategory = "user-mount"` so post-loop diff verification sees them. A traversal cap hit emits `bash_diff_truncated` and sets `diffsPartial = true`.

### Daemon-rootless preference

`bash.rootless: auto | required | forbidden`. `auto` (default) picks rootless when the active daemon reports the `rootless` security option. `required` refuses to start otherwise; `forbidden` refuses when only rootless is reachable. Linux-only — Docker Desktop's VM boundary supersedes on Windows / macOS. Resolved posture is persisted on `BashContainerStartedEvent.DaemonRootless`. `forge doctor` runs `bash.docker.cgroupv2` when rootless is active and warns when `/sys/fs/cgroup/cgroup.controllers` lacks `memory cpu pids`.

When rootless is active, `DockerContainerLifecycle` probes every composed bind-mount host path before `docker run -d`. A non-existent or unreadable path raises `ValidationException` early.

### End-to-end demo

See `examples/bash/` for the reference image + digest-pin recipe and `examples/agents/bash-demo.agent.yaml` for a minimal agent.

## Adding a new built-in

1. Declare `class FooTool : ToolBase<FooInput, FooOutput>` in `Forge.Tools`.
2. Register in `BuiltInToolsRegistration.AddForgeBuiltInTools`: both the singleton AND the `ToolRegistry.Register` line.
3. NEVER use `Console.WriteLine` from a tool — stdout is reserved for the CLI's JSON result. Use `ctx.Logger` (stderr).

## Chat-mode behaviour

When the agent runs under `--chat`, `ask_caller` detects `/exit` (case-insensitive, trimmed) in the user response and throws `ChatExitException`. The runner catches this (gated on `isChat`) and terminates the chat cleanly with an `exited` status. `AskCallerTool.SummarizeCall` provides live tool-call rendering for the chat REPL.

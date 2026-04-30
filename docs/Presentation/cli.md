---
layer: Presentation
title: CLI reference
updated: 2026-04-29
code_refs: [src/Forge.Cli/, src/Forge.Cli/Program.cs, src/Forge.Cli/Commands/AgentCommand.cs, src/Forge.Cli/Commands/ListCommand.cs, src/Forge.Cli/Commands/DescribeCommand.cs, src/Forge.Cli/Commands/ValidateCommand.cs, src/Forge.Cli/Commands/ResumeCommand.cs, src/Forge.Cli/Commands/RunsSubcommands.cs]
related: [Domain/agents.md, Domain/tools.md, Infrastructure/llm-client.md]
---

# `forge` CLI reference

`forge` is a .NET 9 command-line tool for running tool-using agents against a single LiteLLM (OpenAI-compatible) endpoint. This document is the authoritative reference for every command, option, and convention the CLI exposes.

## Command surface

| Command | Purpose |
|---|---|
| `forge agent <name>` | Run a configured agent end-to-end. |
| `forge list [agents \| tools \| all]` | List discovered agents and registered tools. |
| `forge describe <agent \| tool> <name>` | Print machine-readable metadata. |
| `forge validate <path>` | Validate a `*.agent.yaml` file. |
| `forge resume <run-id>` | Resume an interrupted agent run. |
| `forge runs list` / `forge runs show <run-id>` | Inspect run directories. |
| `forge init` | Scaffold `~/.forge/llm.json`. |
| `forge doctor` | Diagnose install / config / endpoint. |
| `forge --help` / `forge --version` | Help text / assembly version. |

There is no `forge run` (no user-facing pipeline surface) and no `forge tool` (deleted in the minimal-forge cut). To invoke a tool directly, write a one-step agent.

## Synopsis

```
forge <command> [arguments] [options]
forge --help
forge --version | -v
```

`forge` is implemented with [Spectre.Console.Cli](https://spectreconsole.net/cli/). Every subcommand supports `--help`.

## Global behaviour

### Output streams

- **stdout** — primary JSON result of the command (the bytes a calling script should pipe).
- **stderr** — logs (info / warning / error) from the .NET logger and any Spectre error markup.

This split is deliberate. Every command that returns data writes a single JSON document to **stdout** and nothing else. `forge ... | jq ...` and `forge ... > result.json` are safe in PowerShell, bash, and CI.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Command succeeded. |
| `1` | User / config error: validation failure (`ValidationException`), config not found, unknown tool, bad flag, missing run id (`ConfigException`). |
| `2` | Runtime failure: `AgentException` (max iterations, missing `submit_final`), `ProviderException` (LiteLLM upstream), `QuotaExhaustedException` (terminal 429), and any unhandled exception. The error payload is written to **stderr** as JSON. |
| `6` | Transient rate-limit exhausted retries (`RateLimitedException`). The run is resumable; wrapper scripts should wait and retry. Stderr JSON includes `errorKind: "rate_limited"`, `resumable: true`, and — when the provider sent a `Retry-After` header — `retryAfterSeconds`. |
| `7` | Caller deferred (`CallerDeferredException`). The run is paused awaiting caller input; resume with `forge resume <run-id> --answer '<json>'`. |
| `130` | Cancelled: Ctrl-C (Windows) or SIGINT/SIGTERM (Unix). `status.json` records `"cancelled"`. The first Ctrl-C sets graceful cancel; a second hard-kills. |

Spectre may also exit non-zero with its own messages on argument-parsing failures.

### Input syntax (`--input` / `-i`)

Every command that takes a JSON payload accepts the same three forms:

| Form | Example | Behaviour |
|---|---|---|
| Inline JSON | `--input "{\"name\":\"Forge\"}"` | The argument is parsed as JSON. |
| File path | `--input @path\to\input.json` | The leading `@` is stripped and the file is read as UTF-8. |
| stdin | `--input=-` (or `-i=-`) | Reads JSON from `stdin` until EOF. |

When `--input` is omitted entirely, the command uses `{}`.

PowerShell / Spectre note: pass stdin with the `=` form (`--input=-`). A bare `-` after a space is parsed as a separate flag and rejected.

## Configuration

`~/.forge/llm.json` carries `baseUrl` (must include `/v1`), `apiKey`, optional `defaultModel`, and an optional `rateLimit` block. Authoritative shape and field semantics live in [Infrastructure/llm-client.md](../Infrastructure/llm-client.md).

| Env var | Effect |
|---|---|
| `FORGE_LLM_BASE_URL` | Overrides `baseUrl`. Trailing `/` stripped. |
| `FORGE_LLM_API_KEY` | Overrides `apiKey` (empty string clears it). |
| `FORGE_LLM_DEFAULT_MODEL` | Overrides `defaultModel`. |
| `FORGE_SEARCH_PROVIDER` | `web_search` provider. Only `brave` in v1. |
| `FORGE_SEARCH_BRAVE_KEY` | Brave Search API key. Required when the tool is invoked. |

If `llm.json` is missing, `forge` falls back to defaults plus env overrides; commands that require an LLM (`forge agent`, `llm_complete`) fail fast at call time.

Agents are discovered in order — first match wins: `<cwd>/.forge/agents/`, `~/.config/forge/agents/`, `~/.forge/agents/`. See [Infrastructure/plugins.md](../Infrastructure/plugins.md).

## Commands

### `forge agent <name>`

Run a single configured agent end-to-end and print its final JSON output.

```
forge agent <name> [-i <JSON_OR_FILE>] [--callers <MODE>] [-H | --human] [--chat]
```

| Argument / option | Required | Description |
|---|---|---|
| `<name>` | Yes | Agent file under any plugin root (`agents/<name>.agent.yaml`). |
| `-i`, `--input` | No | JSON payload validated against the agent's `input_schema`. Defaults to `{}`. |
| `--callers <MODE>` | No | Caller-IO behaviour for headless runs: `auto-deny`, `auto-allow`, `fail-fast`, or a path to an answer-file. |
| `-H`, `--human` | No | Render a human-friendly summary instead of raw JSON. |
| `--chat` | No | Turn the run into a turn-by-turn terminal REPL. Requires a TTY. |

Behaviour:

1. Resolves the agent file via the plugin search order. If not found, prints a red error and exits `1`.
2. Reads `--input` (literal JSON, `@file`, or stdin).
3. Generates a fresh `run-id` (`<agentName>-<timestamp>-<rand>`), creates `~/.forge/runs/<run-id>/` and `stages/agent/`, opens a JSONL trace sink.
4. Runs the agent's tool-calling loop, capping each tool result via `ToolResultCapper` to keep LLM context reasonable.
5. Writes the agent's final JSON output to **stdout** as a single line (or formatted via `--human`).

Example:

```powershell
forge agent hello --input "{\"name\":\"Forge\"}"
```

#### `model:` vs `defaultModel`

- **`model` in `*.agent.yaml`** is sent on every agent completion request as-is. Omitting `model` typically makes LiteLLM error.
- **`defaultModel`** in `~/.forge/llm.json` and **`FORGE_LLM_DEFAULT_MODEL`** populate `LiteLlmConfig.DefaultModel` for callers that fall back when no per-call model is set — notably the `llm_complete` tool when its input omits `model`. They do NOT fill in a missing agent YAML `model:`.

#### Tool loop and `submit_final`

Forge always exposes a `submit_final` tool whose parameters match the agent's `output_schema`. The run succeeds only when the model calls `submit_final` once with JSON arguments that validate against that schema. PREFER explicit "finish by calling `submit_final`" instructions over "reply with JSON" in the system prompt; the latter fights the runner's tool-call nudge.

#### Slow local backends and HTTP timeout

The LiteLLM `HttpClient` uses a 300-second per-request timeout. Very slow first-token or hung local inference can surface as a timeout before the agent completes.

#### Project context, skills, reasoning

Optional agent YAML fields (`inject_project_context`, `inject_skills_catalog`, `project_context_roots`, `reasoning`, `diff_verification`, `compaction`, `caller_io`, `bash`) are documented in [Domain/agents.md](../Domain/agents.md). All default to interactive-friendly values.

### `forge list [agents | tools | all]`

```
forge list                # same as: forge list all
forge list agents
forge list tools
```

| Argument | Required | Description |
|---|---|---|
| `[kind]` | No | `agents`, `tools`, or `all` (default). Invalid values exit `1`. |

Output is JSON on stdout:

```json
{
  "agents": [{"name": "...", "root": "..."}],
  "tools":  [{"name": "...", "description": "..."}]
}
```

Only the requested sections are included. The `root` field is the resolved plugin root, so you can spot shadowing.

`-H` / `--human` renders a Spectre table to the terminal instead.

### `forge describe <agent | tool> <name>`

```
forge describe agent <name>
forge describe tool <name>
```

| Argument | Required | Description |
|---|---|---|
| `<agent \| tool>` | Yes | Kind of object to describe. Other values exit `1`. |
| `<name>` | Yes | Agent YAML file name (without suffix), or registered tool name. |

JSON shape varies by kind:

- `agent` — `kind`, `name`, `model`, `tools[]`, `system_prompt`, `user_prompt`, `input_schema`, `output_schema`, `resolved_path`.
- `tool` — `kind`, `name`, `description`, `input_schema`, `output_schema`.

### `forge validate <path>`

Load and validate an agent YAML file in isolation.

```
forge validate <path>
```

| Argument | Required | Description |
|---|---|---|
| `<path>` | Yes | Path to a `*.agent.yaml` file. |

Output:

- Success: indented JSON on stdout, shape `{"status":"ok","kind":"agent","path":"<abs>"}`. Exit `0`.
- Failure: JSON on **stderr**, shape `{"status":"error","error":"<msg>","cause":"<inner.Message>"}`. Exit `1`.

### `forge runs list`

List run directories under `~/.forge/runs/`, newest first, with the parsed `status.json` inlined.

```
forge runs list
```

Output is a single indented JSON array on **stdout**. `status` is `null` if no `status.json`, `"unreadable"` if it exists but cannot be parsed.

### `forge runs show <run-id>`

```
forge runs show <run-id> [-t | --trace]
```

| Argument / option | Required | Description |
|---|---|---|
| `<run-id>` | Yes | Directory name under `~/.forge/runs/`. Empty / missing exits `1`. |
| `-t`, `--trace` | No | Include `trace_path` and the last 200 lines of `trace.jsonl` as `trace_tail`. |

### `forge resume <run-id>`

Resume an interrupted agent run from the last persisted per-iteration snapshot. `plan.json` records the agent's `pipeline.name` as `"agent:<agent-name>"`; `forge resume` recognises and rehydrates the synthetic single-stage plan.

```
forge resume <run-id> [--force | -f] [-H | --human] [--max-iterations <N>] [--restart-stage agent] [--answer <JSON_OR_FILE>]
```

| Argument / option | Required | Description |
|---|---|---|
| `<run-id>` | Yes | Directory name under `~/.forge/runs/`. Must contain `plan.json`. |
| `--force`, `-f` | No | Continue even if the agent's `schemaHash` has drifted from the value stored in `plan.json`. Without it, drift exits `1`. |
| `-H`, `--human` | No | Render a human-friendly summary. |
| `--max-iterations <N>` | No | Override the agent YAML cap for this resume (typical when a run hit the original cap). |
| `--restart-stage agent` | No | Restart the single agent stage from scratch. |
| `--answer <JSON_OR_FILE>` | No | Supply an answer to the pending caller question (resume from `needs_caller`). |

`forge resume` errors with exit `1` when the underlying run was a non-agent plan (legacy multi-stage runs cannot be resumed by the cut surface).

## Run directory layout

Every command that produces a run creates a subdirectory under `~/.forge/runs/`. See [Data/workspace.md](../Data/workspace.md) for the authoritative inventory.

```
~/.forge/runs/<run-id>/
  input.json
  status.json
  result.json
  trace.jsonl
  plan.json
  stages/
    agent/
      output.json
      tool-outputs/
      iterations/000/
```

`run-id` shape: `<agent-name>-<UTC-timestamp>-<rand>`.

## Built-in tools

`forge list tools` is authoritative. The seven registered built-ins:

| Name | Purpose |
|---|---|
| `fetch_url` | HTTP GET; returns status, content-type, body text. |
| `web_search` | Brave Search API (requires `FORGE_SEARCH_BRAVE_KEY`). |
| `llm_complete` | One-shot LiteLLM completion (no tool loop). Honors per-call `model` / `defaultModel`. |
| `ask_caller` | Pause and ask the caller a question via `ICallerIo`. |
| `notify_caller` | One-way status update to the caller. |
| `request_approval` | Ask the caller to approve a named action. |
| `bash` | Run a single command via `docker exec` inside a per-run sandboxed container. Opt-in. See [Domain/tools.md](../Domain/tools.md#bash-tool). |

Use `forge describe tool <name>` for the exact `input_schema` and `output_schema`.

## `forge agent --chat` details

| Scenario | output_schema | Behaviour |
|---|---|---|
| Pure chat | `null` | No `submit_final`. Tool-less turns prompt the user. `/exit` cleanly exits. |
| Structured agent | present | Identical to non-chat `forge agent`; `submit_final` still required. |

Constraints:
- TTY required. `--chat` with redirected stdin exits `1`.
- Caller policy: `--callers auto-deny` / `fail-fast` / file path exits `1`. `--callers auto-allow` is allowed.
- Input schema bypassed entirely — first message is prompted interactively.

| End state | `status.json` | Exit | `result.json` |
|---|---|---|---|
| `submit_final` | `completed` | 0 | yes |
| `/exit` | `exited` | 0 | no |
| Ctrl-C × 2 | `cancelled` | 130 | no |
| `max_iterations` | `failed` | 1 | no |

Resume rehydrates the conversation from the last state snapshot, re-renders the last assistant message, and prompts for the next user turn:

```bash
forge agent chat-demo --chat --resume <run-id>
```

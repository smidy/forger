# Forge

A **.NET 9** CLI for running **tool-using agents** against a single **LiteLLM** (OpenAI-compatible) endpoint. Forge owns the tool loop, emits JSON to stdout, and writes run artifacts under `~/.forge/runs/`.

Full CLI reference: **[docs/Presentation/cli.md](docs/Presentation/cli.md)**. Architecture entry point: **[docs/index.md](docs/index.md)**.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A running **LiteLLM** proxy (or compatible OpenAI API) reachable from this machine
- **Docker** (only when an agent uses the opt-in `bash` tool)

## Build

```powershell
dotnet build Forge.sln
dotnet test Forge.sln
```

## Install as a global tool

From the repo root (where `Forge.sln` lives):

```powershell
dotnet pack src/Forge.Cli -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts Forge.Cli
```

The packaged command name is **`forge`** (`ToolCommandName` in `Forge.Cli.csproj`). Or run without installing:

```powershell
dotnet run --project src/Forge.Cli -- --help
```

After install, run **`forge init`** to scaffold `~/.forge/llm.json`. See `forge init --help` for flags.

## Configuration (`~/.forge/llm.json`)

Forge reads **`%USERPROFILE%\.forge\llm.json`** (Unix: `~/.forge/llm.json`). Example:

```json
{
  "baseUrl": "http://localhost:4001/v1",
  "apiKey": "sk-your-key",
  "defaultModel": "your-model-id"
}
```

`baseUrl` MUST include the **`/v1`** suffix (the client posts to `{baseUrl}/chat/completions`).

Overrides: `FORGE_LLM_BASE_URL`, `FORGE_LLM_API_KEY`, `FORGE_LLM_DEFAULT_MODEL`. JSON values support **`${ENV_VAR}`** substitution.

## Plugin search order

Agents are discovered from these roots in order; first match wins per file name:

1. **`<cwd>/.forge/`** — project-local
2. **`~/.config/forge/`**
3. **`~/.forge/`** — user home

| Kind | Path pattern |
|------|----------------|
| Agent | `<root>/agents/<name>.agent.yaml` |

## CLI

| Command | Purpose |
|---------|---------|
| `forge --help` / `-v` | Help and version |
| `forge init` | Scaffold `~/.forge/llm.json` |
| `forge doctor [--probe] [--human]` | Diagnose install (config, plugins, optional endpoint probe) |
| `forge agent <name> [-i <json\|@file>]` | Run one agent |
| `forge agent <name> --chat [--resume <id>]` | TTY REPL chat with an agent |
| `forge list [agents\|tools\|all]` | List agents and built-in tools |
| `forge describe <agent\|tool> <name>` | Print JSON metadata (paths, schemas, prompts) |
| `forge validate <path>` | Load and validate `*.agent.yaml` |
| `forge resume <run-id> [-f] [--max-iterations N] [--restart-stage id]` | Resume a previously started agent run |
| `forge runs list` | List `~/.forge/runs/*` with `status.json` when present |
| `forge runs show <run-id> [--trace]` | Show `input` / `status` / `result`; optional last 200 trace lines |

Use **`--input=-`** (or **`-i=-`**) to read JSON from **stdin**. Or pass JSON inline, e.g. **`--input "{}"`**.

Logs go to **stderr**; primary JSON results go to **stdout**.

Per-command arguments, options, exit codes, env vars, and scripting recipes are documented in **[docs/Presentation/cli.md](docs/Presentation/cli.md)**.

## Runs and artifacts

Each agent run creates **`~/.forge/runs/<run-id>/`** with `input.json`, `status.json`, `result.json` on success, `trace.jsonl` (one event per line), and per-iteration snapshots under `stages/agent/iterations/NNN/` (used by `forge resume`).

When an agent enables extended reasoning via `reasoning:` in its YAML (`effort: low|medium|high` and/or `thinking_budget_tokens: >=1024`), per-iteration reasoning text is persisted to `reasoning.txt` and surfaced via the `reasoning_persisted` trace event.

## Repository layout

```
forge/
  Forge.sln
  examples/
  src/
    Forge.Core/      # types, validation, workspace, trace
    Forge.Llm/       # LiteLLM HTTP client + config
    Forge.Tools/     # built-in tools + registry + bash
    Forge.Agent/     # agent YAML + runner
    Forge.Pipeline/  # internal one-stage runner used by `forge agent`
    Forge.Cli/       # Spectre CLI
  tests/
```

## Tool surface

| Tool | Purpose |
|---|---|
| `fetch_url` | HTTP GET |
| `web_search` | Brave Search API |
| `llm_complete` | Nested LLM completion |
| `ask_caller` | Interactive caller question |
| `notify_caller` | Out-of-band caller message |
| `request_approval` | Caller approval gate |
| `bash` | Docker-sandboxed shell, opt-in |

## License

See the containing repository for license terms.

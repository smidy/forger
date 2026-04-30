# Forge examples

Sample agents under `examples/agents/`. Copy each into your search path:

- `examples/agents/<name>.agent.yaml` → `~/.forge/agents/<name>.agent.yaml`

Or use:

```powershell
forge init --copy-examples
```

Validate before invoking:

```powershell
forge validate path\to\hello.agent.yaml
```

## Agent catalog

| Agent | Tools | Purpose |
|---|---|---|
| `hello` | none | Smallest possible structured agent — one LLM call, calls `submit_final` with a greeting |
| `chat-demo` | none | Minimal `--chat` REPL agent (no `output_schema`); demonstrates pure-chat mode |
| `interviewer` | `ask_caller` | Asks the caller 3-5 questions and returns a summary |
| `interviewer-demo` | `ask_caller` | Smaller interviewer used in caller-IO smoke tests |
| `web-researcher` | `web_search`, `fetch_url` | Searches the web for a query and summarises with sources |
| `claim-verifier` | `web_search`, `fetch_url` | Cross-checks claims from a research finding against independent sources |
| `research-scoper` | none | Decomposes a research prompt into 3-6 distinct web search queries |
| `report-writer` | none | Synthesises verified research into a structured markdown report |
| `bash-demo` | `bash` | Sandboxed Docker example — writes a greeting file inside the per-run workspace |

## Running an agent

```powershell
forge agent hello --input "{\"name\":\"Forge\"}"
forge agent chat-demo --chat
forge agent web-researcher --input "{\"query\":\"...\"}"
```

JSON output goes to **stdout**; logs go to **stderr**. Pass `--human` for a Spectre-rendered summary.

## Prerequisites

1. **LiteLLM proxy** with a capable model wired up in `~/.forge/llm.json`. Most agents default to `replace-with-your-model-id`; either swap that for your model id before running, or set `FORGE_LLM_DEFAULT_MODEL` and use `${FORGE_LLM_DEFAULT_MODEL}` in the YAML.
2. **Search provider** for `web_search` — the default is Brave, so set `FORGE_SEARCH_BRAVE_KEY`. Override the provider with `FORGE_SEARCH_PROVIDER`.
3. **Docker >= 25.0** for `bash-demo` (and any other agent declaring `bash:` in its tool list). See [`bash/README.md`](bash/README.md) for image build + digest pin.

## Inspect a run

```powershell
forge runs list --human
forge runs show <run-id> --human
forge runs show <run-id> --trace
```

## Resume a failed run

```powershell
forge resume <run-id>
```

Resume only applies to agent runs (every run is an agent run). If the original run hit `max_iterations`, pass `--max-iterations <N>` to extend.

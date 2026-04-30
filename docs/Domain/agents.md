---
layer: Domain
title: Agents
updated: 2026-04-29
code_refs: [src/Forge.Agent/, src/Forge.Agent/AgentConfig.cs, src/Forge.Agent/YamlFront.cs]
related: [Application/AgentRunner.md, Domain/tools.md, Infrastructure/project-context.md]
---

# Agents

An agent is one tool-using LLM task defined by a YAML file. The whole file is parsed by `YamlFront.ParseToJson` into a `JsonNode`, then mapped to `AgentConfig` via `AgentConfig.FromJsonNode`.

## AgentConfig shape

Required:
- `name` — identifier
- `model` — model name passed to the LiteLLM proxy. Supports `${ENV_VAR}` expansion at load time (e.g. `${FORGE_LLM_DEFAULT_MODEL}`); undefined variables expand to empty.
- `system_prompt` — Scriban template resolved against `RunState`
- `user_prompt` — Scriban template
- `input_schema` — JSON Schema; input validated on entry
- `output_schema` — JSON Schema; drives the synthetic `submit_final` tool

Optional:
- `max_iterations` (default `40`) — hard cap on tool-call rounds
- `tools` — allowlist of registered tool names
- `inject_project_context` (default `true`) — load `AGENTS.md` / `CLAUDE.md` from ordered roots
- `inject_skills_catalog` (default `true`) — append a name+description-only skills appendix to the system prompt
- `project_context_roots` — extra base paths for project markdown
- `reasoning` — `{ effort?, thinking_budget_tokens? }` forwarded to LiteLLM
- `diff_verification` — `{ enabled?, allow_runspace_only? }`; defaults reconcile `submit_final.files_modified` against the write ledger
- `bash` — required when `tools` lists `bash`; see [Domain/tools.md](tools.md#bash-tool)
- `compaction` — opt-in context-compaction config
- `caller_io` — caller-IO budget / behaviour overrides

## Invariants

- Input is validated against `input_schema` before the first LLM call.
- An agent terminates **only** by calling the synthetic `submit_final` tool (injected by `AgentRunner`) with an argument matching `output_schema`.
- Reaching `max_iterations` without `submit_final` → `AgentException`.
- If the model stops calling tools, one nudge ("you must call tools…") is appended; a second silent response → `AgentException`.

## Adding a new agent

1. Drop `<name>.agent.yaml` under any plugin root (`<cwd>/.forge/agents/`, `~/.config/forge/agents/`, `~/.forge/agents/`). First match wins — see [Infrastructure/plugins.md](../Infrastructure/plugins.md).
2. Validate with `forge validate <path>`.
3. Invoke with `forge agent <name> --input '…'`.

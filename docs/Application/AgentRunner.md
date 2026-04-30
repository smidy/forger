---
layer: Application
title: AgentRunner tool loop
updated: 2026-04-29
code_refs: [src/Forge.Agent/AgentRunner.cs, src/Forge.Agent/ToolResultCapper.cs]
related: [Domain/agents.md, Domain/tools.md, Infrastructure/project-context.md]
---

# AgentRunner tool loop

`AgentRunner.RunAsync` executes one agent: builds the system prompt, validates input, iterates tool calls against the LLM, and returns a result validated against `output_schema`.

## Flow

1. **Validate input** against `config.InputSchema` via `Validator.Validate`.
2. **Build system prompt** by concatenating (when enabled):
   - Project markdown (`AGENTS.md` / `CLAUDE.md`) from ordered roots — see [Infrastructure/project-context.md](../Infrastructure/project-context.md)
   - Resolved `system_prompt` (Scriban template over `RunState`)
   - Skills catalog (name + description only) from `SkillCatalog.Build`
3. **Resolve user prompt** (Scriban template over `RunState`).
4. **Build tool specs** from `config.Tools` (registry lookup) plus the synthetic `submit_final` whose `parameters` schema IS `config.OutputSchema`.
5. **Iterate** up to `config.MaxIterations`:
   - Post `chat/completions` via `ILlmClient`
   - Append assistant message to the conversation
   - For each tool call: validate args against `InputSchema`, execute via `ToolRegistry`, cap large results via `ToolResultCapper` (spilled to `stages/<id>/tool-outputs/` on disk, replaced in the message with a pointer), append a tool result message
   - If `submit_final` is called: validate its arg against `config.OutputSchema`, run diff reconciliation against the `AgentWriteLedger`, return the arg
   - If no tool calls this iteration: send one "you must call tools" nudge; a second silent response → `AgentException`

## ToolContext

Passed to every tool. Positional record carrying:

- `RunId`, `RunWorkspace`, `StageDir`, `StageId`, `IterationIndex?`
- `Llm` — the same `ILlmClient` the loop uses (so `llm_complete` re-enters the proxy)
- `Trace` — `ITraceSink` for trace events
- `Logger` — `ILogger` for stderr logs
- `CancellationToken`, `NextToolOutputIdx`
- `WriteLedger?`, `CallerIo?` — set on a per-run basis

There is no filesystem-scope field. Filesystem access is the bash tool's responsibility, gated by `bash.mounts` (see [Domain/tools.md](../Domain/tools.md#mounts-composed-by-mountcomposercompose)).

## Termination conditions

| Condition | Outcome |
|---|---|
| `submit_final` invoked with valid arg | Success — return the arg |
| `submit_final` declared/actual write sets disagree | `AgentDiffMismatchException`; no `result.json` written |
| `max_iterations` reached | `AgentException("…without submit_final.")` |
| Model returns no choices | `AgentException("LLM returned no choices.")` |
| Model stops calling tools twice in a row | `AgentException("…ended without tool calls and without submit_final.")` |

## Diff verification at `submit_final`

After `submit_final` passes schema validation, `AgentRunner` reconciles the `files_modified` array against `ctx.WriteLedger` entries (non-noop writes). Path comparison normalises backslashes to forward slashes and trims leading `/` and `.`. A mismatch (`missing` or `extra`) emits `agent_diff_verification` with `verdict: "reject"` and throws `AgentDiffMismatchException`. Set `agent.diff_verification.enabled: false` to skip the check; `agent.diff_verification.allow_runspace_only: true` exempts agents whose only writes land under the run workspace.

## Error handling within a tool call

Tool exceptions are caught and returned to the model as a tool-result message containing the error payload — they are NOT thrown up immediately. This lets the model observe failures and decide whether to retry, use a different tool, or give up via `submit_final`. Uncaught exceptions only escape if they happen outside a tool-call frame.

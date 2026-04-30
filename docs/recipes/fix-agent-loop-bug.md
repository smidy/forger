---
recipe: fix-agent-loop-bug
audience: debugger
estimated_reads: 2 docs
task: Diagnose and fix a bug in the AgentRunner tool loop
updated: 2026-04-29
---

# Fix an agent-loop bug

## Read first
- `Application/AgentRunner.md` — flow, termination conditions, error handling, diff verification
- `Domain/agents.md` — invariants (`submit_final`, `max_iterations`, the single-nudge behaviour)

## Optional reading
- `Domain/tools.md` — tool contract, schema validation, synthetic `submit_final`
- `Infrastructure/project-context.md` — if the bug involves system-prompt construction

## Touch
- `src/Forge.Agent/AgentRunner.cs` — the tool loop proper (`RunAsync`)
- `src/Forge.Agent/ToolResultCapper.cs` — if the bug relates to large tool-result handling

## Verify
- Reproduce with a minimal agent YAML; capture the failing run's `trace.jsonl` and `status.json`
- After fix: re-run same YAML, confirm `submit_final` terminates the loop as expected
- `dotnet test Forge.sln` — no regressions

## Trace events to inspect
- `tool_result_truncated` — capper firing unexpectedly → look at tool output sizes
- `agent_diff_verification` — `verdict: "reject"` means a write/declared-files mismatch
- `agent_state_snapshot` — emitted once per end-of-iter; path tells you which `state.json` to open

## State snapshots (diagnostic)

Every completed iteration writes `{forgeHome}/runs/<id>/stages/agent/iterations/NNN/state.json` with the full `messages` array and the write `ledger`. This is the fastest way to see **what context the agent had when it gave up**.

- Terminal snapshot (`iterations/<max>/state.json`) — the conversation at the throw point. Read the last `assistant` message for the agent's intent, the last `tool` messages for the errors it could not resolve.
- `ledger[]` — every recorded write in order. Bisect across iterations to find when a wrong write first appeared.

## Common pitfalls
- A silent fallback masks the bug — check whether the "you must call tools" nudge already fired before deeper digging.
- Confusing `input_schema` (entry) vs `output_schema` (`submit_final`) validation — they fire at different points.
- A user-defined tool shadowing `submit_final` → agent can never terminate cleanly.

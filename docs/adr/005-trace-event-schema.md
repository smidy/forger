---
layer: adr
title: Trace event schema — event-per-fact, rename rather than gate
updated: 2026-04-29
code_refs: [src/Forge.Core/Trace/TraceEvent.cs, src/Forge.Core/Trace/TraceSink.cs, src/Forge.Agent/AgentRunner.cs, src/Forge.Core/Workspace/WorkspaceIo.cs]
related: [Infrastructure/logging.md, Data/workspace.md]
---

# 005 Trace event schema — event-per-fact, rename rather than gate

## Status

Accepted (2026-04-22).

## Context

`trace.jsonl` is Forge's durable, post-mortem-readable record of what happened during a run. As the set of things worth recording grew — iteration boundaries, LLM round-trips, tool invocations, bash exec lifecycle — the early minimal schema accumulated two problems:

1. **Vague event names.** Names that described intent rather than observation (e.g. an `fs_access` event emitted before the physical IO) misled post-mortem tooling.
2. **Missing agent-loop coverage.** The original schema had no per-iteration, per-LLM-call, or per-tool-call events. Trace consumers could not answer "how many LLM round-trips?", "what did each tool cost?", "were there redundant calls?" without out-of-band inference from stderr HTTP logs.

## Decision

**Event name = the fact captured, not the intent.** When an event name starts describing a goal rather than an observation, rename it. Backwards-compatibility shims for trace consumers are not worth their weight — the trace is an internal tool, consumers are our own skills, and a one-shot rename beats a decade of footnote caveats.

**One event per meaningful action.** Emit distinct events for distinct happenings: agent iteration boundary, LLM round-trip, tool call completion, bash exec start/end, container lifecycle. Do not coalesce — post-mortem tooling filters are cheaper than reconstruction.

**Compact payloads, SHA-prefixed argument fingerprints for grouping.** A full argument payload would bloat `trace.jsonl` and risk leaking secrets. A SHA-256 prefix (8 bytes / 16 hex chars) is enough for redundancy detection without retaining content.

**No OpenTelemetry spans in v1.** Spans imply a heavier runtime dependency, a span exporter, and distributed-context plumbing Forge does not need. Stay with append-only JSONL; revisit only when a consumer genuinely needs distributed correlation.

## Consequences

- Core agent-loop events: `agent_iteration`, `llm_call` (with `durationMs`, `finishReason`, `promptTokens`, `completionTokens`, and — when the provider reports them — `promptCacheHitTokens` / `promptCacheCreationTokens` / `reasoningTokens`, plus presence flags `reasoningContentPresent` / `thinkingBlocksPresent`), `tool_call` (with `callId`, `toolName`, `argsHash`, `durationMs`, `error?`). `AgentRunner` emits them at obvious points in the loop. Cache-token fields are sourced from `usage.prompt_tokens_details.cached_tokens` (OpenAI shape) or `usage.cache_read_input_tokens` (Anthropic shape); null when the provider omits them. Reasoning tokens come from `usage.completion_tokens_details.reasoning_tokens` and stay null when absent so a reported zero stays distinguishable from "not reported".
- `reasoning_persisted` — emitted once per iteration that produced `reasoning_content` or `thinking_blocks`. Payload: `iteration`, absolute `artifactPath`, `bytes`, `hasThinkingBlocks`. The artifact lives at `runs/<id>/stages/<stageId>/iterations/<NNN>/reasoning.txt` — plaintext, non-atomic, append-only forensic data.
- `agent_diff_verification` — emitted exactly once per agent run, at `submit_final` immediately after schema validation. Payload: `declared` (paths the agent claimed in `files_modified`), `actuallyWritten` (ledger-derived non-no-op write paths), `missing` (declared minus actually-written), `extra` (actually-written minus declared), and `verdict` — `pass`, `reject`, or `skipped`. `reject` means `AgentRunner` threw `AgentDiffMismatchException` and the run does not write `result.json`; `skipped` means `diff_verification.enabled` was false. Path comparison is case-insensitive with forward slashes.
- Bash-tool events: `bash_container_started` / `bash_container_stopped` / `bash_orphan_killed` (lifecycle), `bash_exec_start` / `bash_exec_end` / `bash_exec_error` (per `docker exec`), `bash_diff_truncated` (post-exec scan cap hit), `bash_storage_opt_skipped` (Docker Desktop fallback), `bash_config_error` (parse-time rejection).
- `llm_retry` — emitted once per 429 retry attempt inside `LiteLlmClient`. Payload: `attempt` (1-indexed), `maxAttempts`, `delayMs`, `statusCode` (always 429 in v1), and optional `retryAfterHeader`. Lets post-mortem tooling count transient throttles and attribute delay without inspecting stderr.
- Caller-IO events: `caller_prompt`, `caller_notify`, `caller_approval`, `stage_needs_caller`. Payloads carry lengths and low-cardinality enums — no agent-authored free-form text.
- Compaction events: `context_compacted`, `compaction_skipped`, `compaction_fallback_warning`. Surface what the compactor decided and how the messages list shrank.
- Resume events: `agent_state_snapshot` (per completed iteration), `stage_resumed_from_iter`, `stage_restarted_by_flag`.
- No filesystem-scope events. The `FsScope` subsystem and its `fs_grant_resolved` / `fs_scope_pass` / `fs_denied` events were removed when explicit `bash.mounts` replaced FsScope-derived projection. The C# records are gone; trace consumers should drop these `kind`s entirely.
- Future trace events should follow the same rules: describe a fact, choose a distinct kind name, keep the payload minimal, avoid cross-event correlation fields beyond `iteration` and `callId`. Future renames are cheap — no external contract to preserve.

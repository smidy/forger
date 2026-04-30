---
layer: Infrastructure
title: Logging and tracing
updated: 2026-04-29
code_refs: [src/Forge.Core/Trace/, src/Forge.Cli/Program.cs, src/Forge.Core/Trace/TraceSink.cs, src/Forge.Core/Trace/TraceEvent.cs, src/Forge.Core/Trace/ITraceSink.cs]
related: [adr/002-json-at-every-boundary.md, Data/workspace.md]
---

# Logging and tracing

## Stream contract (CRITICAL)

- **stdout = JSON results only.**
- **stderr = logs** (everything routed through `Microsoft.Extensions.Logging`).

Wired in `Program.cs`: `logging.LogToStandardErrorThreshold = LogLevel.Trace`. Never `Console.WriteLine` log-like text from libraries; never write non-JSON to stdout from commands that produce structured output.

## Trace (per-run structured events)

Every run writes `trace.jsonl` (one JSON event per line) to its run root. Events are polymorphic records deriving from `TraceEvent` in `Forge.Core/Trace/TraceEvent.cs`:

| Event kind | Triggered when |
|---|---|
| `plan_written` | `plan.json` committed at run start |
| `plan_drift` | Resume detected soft-hash mismatch on at least one agent |
| `tool_result_truncated` | `ToolResultCapper` spilled a large tool result to `tool-outputs/` |
| `agent_iteration` | Start of an `AgentRunner` iteration. Payload: `{ index }`. One per assistant turn. |
| `llm_call` | LLM `/chat/completions` round-trip returned. Payload: `{ iteration, durationMs, finishReason?, promptTokens?, completionTokens?, promptCacheHitTokens?, promptCacheCreationTokens?, reasoningTokens?, reasoningContentPresent, thinkingBlocksPresent, purpose? }`. |
| `tool_call` | A tool invocation completed (success or error). Payload: `{ iteration, callId, toolName, argsHash, durationMs, error? }`. `argsHash` is SHA-256 / 8-byte hex prefix — group to detect redundancy. |
| `agent_diff_verification` | Once per run at `submit_final`. Payload: `{ declared, actuallyWritten, missing, extra, verdict }`. `verdict` ∈ `pass | reject | skipped`. |
| `agent_state_snapshot` | After every `iterations/NNN/state.json` write. Payload: `{ iteration, path, bytes }`. |
| `reasoning_persisted` | Per iteration that produced reasoning content. Payload: `{ iteration, artifactPath, bytes, hasThinkingBlocks }`. |
| `llm_retry` | Per 429 retry inside `LiteLlmClient`. Payload: `{ attempt, maxAttempts, delayMs, statusCode, retryAfterHeader? }`. |
| `bash_container_started` | Per-run bash container ready. Payload includes `imageDigest`, `mounts`, `daemonRootless?`. |
| `bash_container_stopped` | Container stopped at agent-loop end. |
| `bash_orphan_killed` | Janitor removed a stale `forge.run=*` container at process start. |
| `bash_exec_start` / `bash_exec_end` | Per `docker exec` call. End payload: `{ exitCode, durationMs, truncated, diffCount }`. |
| `bash_exec_error` | Daemon-level failure (timeout, hard-kill, daemon gone). |
| `bash_diff_truncated` | Diff scan hit a traversal cap (`max_files`, `max_depth`, `max_hash_bytes`). |
| `bash_storage_opt_skipped` | Daemon rejected `--storage-opt`; retried without it. |
| `bash_config_error` | `bash:` block rejected at lifecycle start. |
| `caller_prompt` / `caller_notify` / `caller_approval` | Per `ask_caller` / `notify_caller` / `request_approval`. Lengths only — no free-form text. |
| `stage_needs_caller` | Stage transitioned to `needs_caller` (`PipelineExecutor`). |
| `chat_exit` / `user_interjection` | Chat-mode REPL boundaries. |
| `context_compacted` / `compaction_skipped` / `compaction_fallback_warning` | Context-compaction outcomes. |
| `stage_resumed_from_iter` / `stage_restarted_by_flag` | Resumer-driven stage state changes. |
| `generic` | Freeform payload; use sparingly. |

There are no `fs_*` events — filesystem-scope tracing was removed when `FsScope` was deleted.

Sink: `TraceSink` — async, bounded channel, flushed on disposal (`await using`). Lines flush per write so a non-graceful shutdown leaves whole lines on disk. Never block the run loop on trace writes.

## Event discipline

- One event per meaningful action; avoid chatter.
- Payloads serialise with `JsonSerializationDefaults.Trace` (stable camelCase).
- Adding a new event type → add a record in `Forge.Core/Trace/TraceEvent.cs`; do not reuse `GenericTraceEvent`. See [recipes/add-trace-event.md](../recipes/add-trace-event.md).

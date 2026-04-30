using System.Text.Json.Nodes;
using System.Collections.Generic;

namespace Forge.Core.Trace;

public abstract record TraceEvent
{
  public abstract string Kind { get; }
  public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PlanWrittenEvent : TraceEvent
{
  public override string Kind => "plan_written";
}

public sealed record ToolResultTruncatedEvent : TraceEvent
{
  public override string Kind => "tool_result_truncated";
  public required string StageId { get; init; }
  public required int Bytes { get; init; }
  public required string ArtifactPath { get; init; }
}

public sealed record PlanDriftEvent : TraceEvent
{
  public override string Kind => "plan_drift";
  public required IReadOnlyList<string> ChangedAgents { get; init; }
}

public sealed record GenericTraceEvent : TraceEvent
{
  public override string Kind => "generic";
  public required JsonNode Payload { get; init; }
}

// Agent-loop instrumentation. Emitted by AgentRunner so post-mortem tooling
// can count iterations, build tool histograms, detect redundant calls, and
// attribute wall time to LLM vs tool work.
public sealed record AgentIterationEvent : TraceEvent
{
  public override string Kind => "agent_iteration";
  public required int Index { get; init; }
}

public sealed record LlmCallEvent : TraceEvent
{
  public override string Kind => "llm_call";
  public required int Iteration { get; init; }
  public required long DurationMs { get; init; }
  public string? FinishReason { get; init; }
  public int? PromptTokens { get; init; }
  public int? CompletionTokens { get; init; }
  // Null when the provider did not report a cache hit (short prefix, cache miss,
  // or provider that does not return cache metadata). Non-null even when zero —
  // a zero lets post-mortem tooling distinguish "provider reported, no hit" from
  // "provider did not report". Sourced from `usage.prompt_tokens_details.cached_tokens`
  // (OpenAI shape) or `usage.cache_read_input_tokens` (Anthropic shape) in LiteLlmClient.
  public int? PromptCacheHitTokens { get; init; }
  // Anthropic-only: tokens written to cache on this call. Null for providers
  // (DeepSeek/Qwen/Kimi/GLM/Gemini) that bill cache writes as normal input.
  public int? PromptCacheCreationTokens { get; init; }
  // Reasoning-token spend sourced from `usage.completion_tokens_details.reasoning_tokens`.
  // Null when the provider omits the field (reasoning not enabled or provider
  // does not report). A reported zero stays distinct from absent.
  public int? ReasoningTokens { get; init; }
  // True when the assistant message carried non-empty `reasoning_content`.
  public bool ReasoningContentPresent { get; init; }
  // True when the assistant message carried any `thinking_blocks` (Anthropic
  // extended-thinking). Tracked separately from `reasoning_content` because
  // providers may return either, both, or neither.
  public bool ThinkingBlocksPresent { get; init; }
  /// <summary>
  /// Optional tag for trace-consumer tooling to segment summariser LLM calls
  /// from agent LLM calls. Null for ordinary agent turns.
  /// </summary>
  public string? Purpose { get; init; }
}

// Emitted once per iteration that persisted a reasoning.txt artifact. Consumers
// (eval harness, analyser scripts) can join iteration -> reasoning file without
// probing the filesystem. See docs/plans/agent-reasoning.md.
public sealed record ReasoningPersistedEvent : TraceEvent
{
  public override string Kind => "reasoning_persisted";
  public required int Iteration { get; init; }
  public required string ArtifactPath { get; init; }   // absolute
  public required int Bytes { get; init; }
  public required bool HasThinkingBlocks { get; init; }
}

public sealed record ToolCallEvent : TraceEvent
{
  public override string Kind => "tool_call";
  public required int Iteration { get; init; }
  public required string CallId { get; init; }
  public required string ToolName { get; init; }
  // SHA256 prefix (16 hex chars) of the raw arguments JSON. Lets post-mortem
  // tooling group redundant calls without leaking argument content.
  public required string ArgsHash { get; init; }
  public required long DurationMs { get; init; }
  public string? Error { get; init; }
}

// Emitted by AgentRunner exactly once per run, at submit_final, after the
// output payload passes schema validation. Surfaces the result of reconciling
// `files_modified` from the payload against the write ledger so post-mortem
// tooling and evals can spot drift without re-running git.
//
// `verdict` is one of:
//   - `pass`    — declared and actually-written sets agreed (or the agent's
//                 diff_verification config allowed the run to skip checking,
//                 e.g. allow_runspace_only with zero repo writes).
//   - `reject`  — sets disagreed; AgentRunner threw AgentDiffMismatchException
//                 and the run does not write result.json.
//   - `skipped` — diff_verification.enabled was false; the event is emitted
//                 purely for visibility so the trace still records why no
//                 check ran.
public sealed record AgentDiffVerificationEvent : TraceEvent
{
  public override string Kind => "agent_diff_verification";
  public required IReadOnlyList<string> Declared { get; init; }
  public required IReadOnlyList<string> ActuallyWritten { get; init; }
  public required IReadOnlyList<string> Missing { get; init; }
  public required IReadOnlyList<string> Extra { get; init; }
  public required string Verdict { get; init; }
}

/// <summary>
/// Emitted after writing an agent state snapshot (<c>iterations/NNN/state.json</c>).
/// </summary>
public sealed record AgentStateSnapshotEvent : TraceEvent
{
  public override string Kind => "agent_state_snapshot";
  public required int Iteration { get; init; }
  public required string Path { get; init; }
  public required long Bytes { get; init; }
}

/// <summary>
/// Emitted when <c>Resumer</c> loads a snapshot for a stage that previously
/// exhausted <c>max_iterations</c> and will resume from a specific iteration.
/// </summary>
public sealed record StageResumedFromIterEvent : TraceEvent
{
  public override string Kind => "stage_resumed_from_iter";
  public required string StageId { get; init; }
  public required int FromIter { get; init; }
  public required string SourcePath { get; init; }
}

public sealed record StageRestartedByFlagEvent : TraceEvent
{
  public override string Kind => "stage_restarted_by_flag";
  public required string StageId { get; init; }
}

// ─── bash-tool events (docs/plans/bash-tool.md) ──────────────────────────────

/// <summary>
/// Emitted once per agent run when the bash tool's per-run Docker container has
/// started and is ready for <c>docker exec</c>. Carries the resolved image
/// digest so post-mortem tooling can reconstruct which binary actually ran.
/// </summary>
public sealed record BashContainerStartedEvent : TraceEvent
{
  public override string Kind => "bash_container_started";
  public required string RunId { get; init; }
  public required string ContainerName { get; init; }
  public required string ContainerId { get; init; }
  public required string ImageRef { get; init; }
  public required string ImageDigest { get; init; }
  public required string Network { get; init; }
  public required IReadOnlyList<string> Mounts { get; init; }

  /// <summary>
  /// Whether the active Docker daemon reported the <c>rootless</c> security
  /// option at container-start time. Optional (nullable) to preserve
  /// compatibility with trace consumers written before the rootless probe
  /// landed. Plan: <c>docs/plans/bash-tool-rootless-docker.md</c>.
  /// </summary>
  public bool? DaemonRootless { get; init; }
}

/// <summary>Emitted when the bash tool's container is stopped at agent-loop end.</summary>
public sealed record BashContainerStoppedEvent : TraceEvent
{
  public override string Kind => "bash_container_stopped";
  public required string RunId { get; init; }
  public required string ContainerName { get; init; }
  public required string Reason { get; init; }
}

/// <summary>Emitted when the janitor removes a stale <c>forge.run=*</c> container at process start.</summary>
public sealed record BashOrphanKilledEvent : TraceEvent
{
  public override string Kind => "bash_orphan_killed";
  public required string ContainerName { get; init; }
  public required string ContainerId { get; init; }
}

/// <summary>Emitted immediately before <c>docker exec</c> starts.</summary>
public sealed record BashExecStartEvent : TraceEvent
{
  public override string Kind => "bash_exec_start";
  public required string RunId { get; init; }
  public required string CommandHash { get; init; }
  public required string Cwd { get; init; }
  public required int TimeoutSec { get; init; }
}

/// <summary>Emitted when a <c>docker exec</c> call returns (including timeout and non-zero exits).</summary>
public sealed record BashExecEndEvent : TraceEvent
{
  public override string Kind => "bash_exec_end";
  public required string RunId { get; init; }
  public required int ExitCode { get; init; }
  public required long DurationMs { get; init; }
  public required bool Truncated { get; init; }
  public required int DiffCount { get; init; }
}

/// <summary>Emitted when a <c>bash:</c> config block is rejected at lifecycle start.</summary>
public sealed record BashConfigErrorEvent : TraceEvent
{
  public override string Kind => "bash_config_error";
  public required string RunId { get; init; }
  public required string Reason { get; init; }
}

/// <summary>Emitted when the exec fails at the Docker level (daemon gone, stream-cap kill, timeout).</summary>
public sealed record BashExecErrorEvent : TraceEvent
{
  public override string Kind => "bash_exec_error";
  public required string RunId { get; init; }
  public required string Reason { get; init; }
  public string? StderrTail { get; init; }
}

/// <summary>
/// Emitted when the diff scanner hit a bounded-traversal cap (max_files,
/// max_depth, or max_hash_bytes truncation on very large files). The tool
/// call's <c>diffs</c> list is flagged as partial.
/// </summary>
public sealed record BashDiffTruncatedEvent : TraceEvent
{
  public override string Kind => "bash_diff_truncated";
  public required string RunId { get; init; }
  public required string Reason { get; init; }
  public required string Root { get; init; }
  public int? FilesScanned { get; init; }
  public int? MaxDepthReached { get; init; }
}

/// <summary>
/// Emitted when <c>docker run</c> rejects <c>--storage-opt</c> because the
/// active storage driver does not support it (typically Docker Desktop on
/// macOS/Windows). Forge retries the run with the flag stripped, so this is a
/// warning rather than a hard failure. The trace preserves the original value
/// for auditability — a production Linux host that drops the flag loses disk-
/// quota defense-in-depth and should be flagged at review time.
/// </summary>
public sealed record BashStorageOptSkippedEvent : TraceEvent
{
  public override string Kind => "bash_storage_opt_skipped";
  public required string RunId { get; init; }
  public required string OriginalStorageOpt { get; init; }
  public required string DockerStderr { get; init; }
}

// ─── Compaction events (docs/plans/context-auto-compaction.md) ──────────────

/// <summary>
/// Emitted when agent context compaction has been applied to the messages list,
/// replacing stale tool-result content with disk-backed stubs to keep the
/// conversation under the model's context limit.
/// </summary>
public sealed record ContextCompactedEvent : TraceEvent
{
  public override string Kind => "context_compacted";
  public required int Iteration { get; init; }
  public required string Strategy { get; init; }
  public required int MessagesBefore { get; init; }
  public required int MessagesAfter { get; init; }
  public required int EstimatedTokensBefore { get; init; }
  public required int EstimatedTokensAfter { get; init; }
  public required IReadOnlyList<int> CompactedIterations { get; init; }
  public string? ArchivePath { get; init; }

  /// <summary>Summariser model used by the <c>summarise</c> strategy. Null for other strategies.</summary>
  public string? SummariserModel { get; init; }

  /// <summary>
  /// Token counts from the summariser LLM call (<c>prompt</c> and <c>completion</c>).
  /// Non-null only for the <c>summarise</c> strategy. Sourced from the summariser's
  /// <see cref="LlmCallEvent"/>.
  /// </summary>
  public SummariserTokensPayload? SummariserTokens { get; init; }
}

/// <summary>
/// Emitted when a compaction pass was skipped for a reason that should be
/// visible in post-mortem tooling, e.g. an invariant violation or no
/// compaction candidates found. The agent continues with the uncompacted
/// messages list.
/// </summary>
public sealed record CompactionSkippedEvent : TraceEvent
{
  public override string Kind => "compaction_skipped";
  public required int Iteration { get; init; }
  public required string Reason { get; init; }
}

/// <summary>
/// Emitted once per agent run to warn that the configured model is not in
/// the <c>model_context</c> map of <c>~/.forge/llm.json</c> and compaction
/// has fallen back to the default threshold of 100_000 tokens. Does not
/// repeat for the same model in the same run.
/// </summary>
public sealed record CompactionFallbackWarningEvent : TraceEvent
{
  public override string Kind => "compaction_fallback_warning";
  public required string Model { get; init; }
  public required int FallbackThreshold { get; init; }
}

/// <summary>Token counts from a summariser LLM call during <c>summarise</c>-strategy compaction.</summary>
public sealed record SummariserTokensPayload
{
  public required int Prompt { get; init; }
  public required int Completion { get; init; }
}

// NOTE: LlmCallEvent.Purpose field — added above the ThinkingBlocksPresent line.

/// <summary>
/// Emitted once per 429 retry attempt inside <c>LiteLlmClient</c>. Lets post-
/// mortem tooling (eval harness, analyser script) count transient provider
/// throttles and attribute delay without inspecting stderr logs.
/// </summary>
public sealed record LlmRetryEvent : TraceEvent
{
  public override string Kind => "llm_retry";

  /// <summary>1-indexed retry attempt number.</summary>
  public required int Attempt { get; init; }

  public required int MaxAttempts { get; init; }
  public required int DelayMs { get; init; }

  /// <summary>Always 429 in v1.</summary>
  public required int StatusCode { get; init; }
  public string? RetryAfterHeader { get; init; }
}

// ─── Caller-IO events (docs/plans/caller-io.md) ──────────────────────────────

/// <summary>
/// Emitted when the agent calls <c>ask_caller</c>. Contains only length metadata —
/// prompt/response text lives in <c>state.json</c> and <c>pending_question.json</c>,
/// not in the trace.
/// </summary>
public sealed record CallerPromptEvent : TraceEvent
{
  public override string Kind => "caller_prompt";
  public required int Iteration { get; init; }
  public required int QuestionLength { get; init; }
  public required int ResponseLength { get; init; }
  public required int ChoicesCount { get; init; }
  public required bool Resumed { get; init; }
}

/// <summary>
/// Emitted when the agent calls <c>notify_caller</c>. Summary/detail text is
/// replaced with lengths to keep the trace free of agent-authored free-form text.
/// </summary>
public sealed record CallerNotifyEvent : TraceEvent
{
  public override string Kind => "caller_notify";
  public required int Iteration { get; init; }
  public required string Level { get; init; }
  public required int SummaryLength { get; init; }
  public required int DetailLength { get; init; }
}

/// <summary>
/// Emitted when the agent calls <c>request_approval</c>. The action name IS
/// recorded (low-cardinality, not user-authored free-form). No summary text.
/// </summary>
public sealed record CallerApprovalEvent : TraceEvent
{
  public override string Kind => "caller_approval";
  public required int Iteration { get; init; }
  public required string Action { get; init; }
  public required string Risk { get; init; }
  public required bool Allowed { get; init; }
  public required string DecisionReason { get; init; }
}

/// <summary>
/// Emitted by <c>PipelineExecutor</c> when a stage transitions to <c>needs_caller</c>.
/// </summary>
public sealed record StageNeedsCallerEvent : TraceEvent
{
  public override string Kind => "stage_needs_caller";
  public required string StageId { get; init; }
  public required string QuestionPath { get; init; }
}

/// <summary>Emitted when a chat-mode agent run ends.</summary>
public sealed record ChatExitEvent : TraceEvent
{
  public override string Kind => "chat_exit";
  public required string Reason { get; init; }
  public required int Iteration { get; init; }
}

/// <summary>Emitted when the user injects a nudge at the top of a chat-mode iteration.</summary>
public sealed record UserInterjectionEvent : TraceEvent
{
  public override string Kind => "user_interjection";
  public required int Iteration { get; init; }
  public required int Length { get; init; }
}

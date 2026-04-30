using System.Text.Json.Nodes;

namespace Forge.Core.Types;

/// <summary>
/// Abstraction over the caller (terminal user, pipeline executor, or CI harness)
/// that lets running agents ask questions, raise notifications, and request
/// approval for guarded actions mid-run. Four transports: ConsoleCallerIo,
/// HeadlessCallerIo, PreAnsweredCallerIo, and a future MCP JSON-lines transport.
/// </summary>
public interface ICallerIo
{
  /// <summary>
  /// Ask the caller a question; blocks until a response is available.
  /// Headless callers may defer (throw <see cref="Exceptions.CallerDeferredException"/>)
  /// or fail-fast depending on <see cref="CallerPolicy"/>.
  /// </summary>
  Task<CallerPromptResponse> PromptAsync(CallerPrompt prompt, CancellationToken ct);

  /// <summary>
  /// Fire-and-forget notification. Never blocks the agent loop.
  /// </summary>
  Task NotifyAsync(CallerNotice notice, CancellationToken ct);

  /// <summary>
  /// Request approval for a guarded action; blocks until a decision is reached.
  /// Headless callers resolve via <see cref="CallerPolicy"/>.
  /// </summary>
  Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct);

  /// <summary>
  /// Write assistant-chat text to the caller's display. Chat-mode surface from
  /// interactive-chat plan; inert on headless transports.
  /// </summary>
  Task WriteAssistantTextAsync(string text, CancellationToken ct);

  /// <summary>
  /// Write a one-line tool-call summary for the caller's display. Chat-mode surface;
  /// inert on headless transports.
  /// </summary>
  Task WriteToolCallSummaryAsync(int iteration, string toolName, string summary, bool isError, CancellationToken ct);

  /// <summary>
  /// Write a system-level notice (e.g. "Compacting context…") to the caller's display.
  /// Chat-mode surface; inert on headless transports.
  /// </summary>
  Task WriteSystemNoticeAsync(string notice, CancellationToken ct);

  /// <summary>
  /// Non-blocking poll for an interjection line typed by the user while the agent
  /// was running. Returns null when no input is waiting. Chat-mode surface;
  /// always returns null on headless transports.
  /// </summary>
  string? TryTakeUserInterjection();
}

/// <summary>
/// A question posed to the caller via <c>ask_caller</c>.
/// </summary>
public sealed record CallerPrompt
{
  /// <summary>The question text.</summary>
  public required string Question { get; init; }

  /// <summary>Optional hint rendered below the question.</summary>
  public string? Hint { get; init; }

  /// <summary>
  /// Optional constrained choices. When non-null and non-empty, the caller
  /// should be presented with a numbered list. Null means free-form response.
  /// </summary>
  public IReadOnlyList<string>? Choices { get; init; }

  /// <summary>The tool that originated the prompt, e.g. "ask_caller".</summary>
  public string? SourceToolName { get; init; }
}

/// <summary>
/// The caller's response to an <c>ask_caller</c> prompt.
/// </summary>
public sealed record CallerPromptResponse
{
  /// <summary>The free-form text response from the caller.</summary>
  public required string Response { get; init; }
}

/// <summary>
/// Severity level for <c>notify_caller</c> notifications.
/// </summary>
public enum NoticeLevel
{
  /// <summary>Informational; low importance.</summary>
  Info,

  /// <summary>Warning; something the caller should know.</summary>
  Warn,

  /// <summary>Error condition; notable but not fatal to the run.</summary>
  Error
}

/// <summary>
/// A one-shot notification from <c>notify_caller</c>.
/// </summary>
public sealed record CallerNotice
{
  /// <summary>Severity level.</summary>
  public required NoticeLevel Level { get; init; }

  /// <summary>Short one-line summary.</summary>
  public required string Summary { get; init; }

  /// <summary>Optional longer detail.</summary>
  public string? Detail { get; init; }
}

/// <summary>
/// Risk level for <c>request_approval</c> actions.
/// </summary>
public enum RiskLevel
{
  /// <summary>Risk not assessed.</summary>
  Unknown,

  /// <summary>Low blast radius; default highlight on "yes".</summary>
  Low,

  /// <summary>Moderate blast radius.</summary>
  Medium,

  /// <summary>High blast radius; default highlight on "no".</summary>
  High
}

/// <summary>
/// A request for approval of a guarded action via <c>request_approval</c>.
/// </summary>
public sealed record ApprovalRequest
{
  /// <summary>
  /// The action being requested, e.g. "delete_files", "force_push", "run_bash_&lt;image&gt;".
  /// Open set; any string is legal.
  /// </summary>
  public required string Action { get; init; }

  /// <summary>Human-readable summary of what is being approved.</summary>
  public required string Summary { get; init; }

  /// <summary>Risk assessment.</summary>
  public RiskLevel Risk { get; init; } = RiskLevel.Unknown;

  /// <summary>
  /// Optional tool-specific structured context (e.g. file list for delete_files).
  /// </summary>
  public IReadOnlyDictionary<string, JsonNode>? Context { get; init; }
}

/// <summary>
/// A decision on an <c>request_approval</c> request.
/// </summary>
public sealed record ApprovalDecision
{
  /// <summary>Whether the action is allowed.</summary>
  public required bool Allowed { get; init; }

  /// <summary>
  /// Human-readable reason, e.g. "auto-deny (policy)", "user-approved", "timeout".
  /// </summary>
  public string? Reason { get; init; }
}

namespace Forge.Agent.Compaction;

/// <summary>
/// Versioned system prompt for the <c>summarise</c> compaction strategy.
/// The version tag (<c>v1</c>) is emitted in the trace event so eval tooling
/// can correlate summary quality against prompt version without reading the
/// prompt itself. Update the version when the prompt changes substantively.
/// </summary>
internal static class SummarisePrompt
{
  /// <summary>Prompt version tag for trace attribution.</summary>
  internal const string Version = "v1";

  internal static string GetSystemPrompt()
  {
    return $"""
      You are a conversation-compaction assistant (version {Version}). Your job is to read
      the following iteration history of an AI agent's tool-use session and produce ONE
      concise prose summary that captures everything the agent needs to continue working
      correctly — preserving decisions made, files touched, and open questions — while
      discarding implementation details that are no longer relevant.

      Rules:
      - DO NOT repeat the full history. Produce a single tight paragraph (max ~500 words).
      - Preserve: key decisions (e.g. "switched from Plan A to Plan B because X"),
        every file path the agent wrote or read, every error encountered, every
        open question or unresolved issue.
      - Discard: exact tool arguments, large data dumps, minor retries, formatting
        of tool results, intermediate debugging output.
      - If the agent is mid-iteration (the last assistant message has tool_calls but
        their results are not yet in the history), mention the in-flight tool calls
        and what the agent was trying to do.
      - Output plain text only — no JSON, no markdown headings.

      The summary will be injected as a synthetic assistant+tool pair replacing the
      iterations you summarise. A future LLM call on the same conversation will read
      this summary to understand what happened. Make it self-contained and actionable.
      """;
  }
}

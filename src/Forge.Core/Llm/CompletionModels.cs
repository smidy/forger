using System.Text.Json.Nodes;

namespace Forge.Core.Llm;

/// <summary>OpenAI-compatible chat completion request (LiteLLM proxy).</summary>
public sealed class CompletionRequest
{
  public string Model { get; set; } = "";
  public List<JsonNode> Messages { get; set; } = new();
  public List<ToolSpec>? Tools { get; set; }
  public object? ToolChoice { get; set; }
  public int MaxTokens { get; set; } = 4096;
  // Null when the agent does not opt in. Serialised as top-level `reasoning_effort`;
  // LiteLLM forwards to providers that support it (Anthropic, DeepSeek, OpenAI, Gemini)
  // and drops the key for providers that do not. See docs/plans/agent-reasoning.md.
  public string? ReasoningEffort { get; set; }
  // Anthropic-only extended-thinking budget. Serialised as
  // `thinking: {type: "enabled", budget_tokens: N}`; LiteLLM drops the object for
  // other providers. Null disables.
  public int? ThinkingBudgetTokens { get; set; }
}

public sealed class ToolSpec
{
  public string Type { get; set; } = "function";
  public FunctionToolSpec Function { get; set; } = null!;
}

public sealed class FunctionToolSpec
{
  public string Name { get; set; } = "";
  public string? Description { get; set; }
  public JsonNode Parameters { get; set; } = new JsonObject();
}

public sealed class CompletionResponse
{
  public string Id { get; set; } = "";
  public List<CompletionChoice> Choices { get; set; } = new();
  public UsagePayload? Usage { get; set; }
}

public sealed class CompletionChoice
{
  public int Index { get; set; }
  public ChatMessagePayload Message { get; set; } = null!;
  public string? FinishReason { get; set; }
}

public sealed class ChatMessagePayload
{
  public string Role { get; set; } = "assistant";
  public JsonNode? Content { get; set; }
  public List<ToolCallPayload>? ToolCalls { get; set; }
  public JsonNode? ReasoningContent { get; set; }
  public JsonArray? ThinkingBlocks { get; set; }
}

public sealed class ToolCallPayload
{
  public string Id { get; set; } = "";
  public string Type { get; set; } = "function";
  public FunctionCallPayload Function { get; set; } = null!;
}

public sealed class FunctionCallPayload
{
  public string Name { get; set; } = "";
  public string Arguments { get; set; } = "{}";
}

public sealed class UsagePayload
{
  public int PromptTokens { get; set; }
  public int CompletionTokens { get; set; }
  public int? PromptCacheHitTokens { get; set; }
  public int? PromptCacheCreationTokens { get; set; }
  // Non-null when the provider reports reasoning token spend via
  // `usage.completion_tokens_details.reasoning_tokens`. Null when omitted — matches
  // the nullable pattern used for cache tokens so "reported zero" stays distinct
  // from "not reported".
  public int? ReasoningTokens { get; set; }
}

public interface ILlmClient
{
  Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Llm;
using Forge.Core.Types;
using Forge.Llm;

namespace Forge.Tools;

public sealed class LlmCompleteInput
{
  public required string Prompt { get; init; }
  public string? Model { get; init; }
}

public sealed class LlmCompleteOutput
{
  public required string Text { get; init; }
  public required JsonNode Usage { get; init; }
}

public sealed class LlmCompleteTool : ToolBase<LlmCompleteInput, LlmCompleteOutput>
{
  private readonly LiteLlmConfig _cfg;

  public LlmCompleteTool(LiteLlmConfig cfg) => _cfg = cfg;

  public override string Name => "llm_complete";
  public override string Description => "One-shot completion via the configured LiteLLM proxy (no tools).";

  protected override async Task<LlmCompleteOutput> ExecuteCoreAsync(LlmCompleteInput input, ToolContext ctx, CancellationToken cancellationToken)
  {
    var model = input.Model ?? _cfg.DefaultModel;
    if (string.IsNullOrEmpty(model))
    {
      throw new InvalidOperationException("No model: set in agent config, llm.json defaultModel, or tool input.");
    }

    var req = new CompletionRequest
    {
      Model = model,
      MaxTokens = 4096,
      Messages = new List<JsonNode>
      {
        new JsonObject { ["role"] = "user", ["content"] = input.Prompt }
      }
    };

    var resp = await ctx.Llm.CompleteAsync(req, cancellationToken).ConfigureAwait(false);
    var text = resp.Choices.FirstOrDefault()?.Message.Content switch
    {
      JsonValue v when v.TryGetValue(out string? s) => s,
      JsonArray a => a.ToJsonString(),
      null => "",
      var n => n.ToJsonString()
    };

    var usageNode = resp.Usage is { } u
      ? JsonSerializer.SerializeToNode(new
      {
        prompt_tokens = u.PromptTokens,
        completion_tokens = u.CompletionTokens,
        cache = new { read = u.PromptCacheHitTokens, creation = u.PromptCacheCreationTokens }
      })!
      : new JsonObject();

    return new LlmCompleteOutput { Text = text ?? "", Usage = usageNode };
  }
}

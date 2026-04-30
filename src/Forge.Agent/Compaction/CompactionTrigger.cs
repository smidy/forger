using System.Text.Json.Nodes;
using Forge.Llm;

namespace Forge.Agent.Compaction;

/// <summary>
/// Decides whether context compaction should fire before the next LLM call.
/// Threshold priority: explicit <c>token_threshold</c> → <c>pct_of_model_limit</c>
/// × model context → <see cref="DefaultFallbackTokens"/>.
/// </summary>
internal static class CompactionTrigger
{
  public const int DefaultFallbackTokens = 100_000;
  public const double DefaultPctOfModelLimit = 0.75;

  public static bool ShouldCompact(
    IReadOnlyList<JsonNode> messages,
    AgentCompactionConfig cfg,
    string model,
    LiteLlmConfig llmCfg,
    int? lastActualPromptTokens,
    int? lastEstimatedTokens,
    out int? fallbackThreshold)
  {
    fallbackThreshold = null;
    if (!cfg.Enabled) return false;

    var est = TokenEstimator.Estimate(messages);
    if (lastActualPromptTokens is not null && lastEstimatedTokens is not null)
    {
      est = TokenEstimator.ApplyCorrection(est, lastActualPromptTokens, lastEstimatedTokens.Value);
    }

    var threshold = ResolveThreshold(cfg, model, llmCfg, out fallbackThreshold);
    return est >= threshold;
  }

  public static bool ShouldCompact(
    IReadOnlyList<JsonNode> messages,
    AgentCompactionConfig cfg,
    string model,
    LiteLlmConfig llmCfg,
    int? lastActualPromptTokens,
    int? lastEstimatedTokens)
    => ShouldCompact(messages, cfg, model, llmCfg, lastActualPromptTokens, lastEstimatedTokens, out _);

  public static int ResolveThreshold(
    AgentCompactionConfig cfg,
    string model,
    LiteLlmConfig llmCfg,
    out int? fallbackThreshold)
  {
    fallbackThreshold = null;

    if (cfg.TokenThreshold is int tt) return tt;

    int modelContext;
    if (llmCfg.ModelContext.TryGetValue(model, out var ctx))
    {
      modelContext = ctx;
    }
    else
    {
      fallbackThreshold = DefaultFallbackTokens;
      modelContext = DefaultFallbackTokens;
    }

    var pct = cfg.PctOfModelLimit ?? DefaultPctOfModelLimit;
    return Math.Max((int)(modelContext * pct), 1);
  }
}

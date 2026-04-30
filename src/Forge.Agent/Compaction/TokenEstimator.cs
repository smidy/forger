using System.Text.Json.Nodes;
using Forge.Core.Json;

namespace Forge.Agent.Compaction;

/// <summary>
/// Char-count / 4 token estimator with a one-iteration-lag correction factor.
/// Serialises with <see cref="JsonSerializationDefaults.CamelCaseTool"/> so the
/// estimate mirrors what the LLM actually sees on the wire.
/// </summary>
internal static class TokenEstimator
{
  private const int PerMessageOverhead = 4;
  private const double CharsPerToken = 4.0;

  public static int Estimate(IReadOnlyList<JsonNode> messages)
  {
    var totalChars = 0L;
    foreach (var msg in messages)
    {
      totalChars += msg.ToJsonString(JsonSerializationDefaults.CamelCaseTool).Length;
    }
    var estimated = (int)(totalChars / CharsPerToken) + messages.Count * PerMessageOverhead;
    return Math.Max(estimated, 1);
  }

  public static int ApplyCorrection(int estimated, int? lastActualPromptTokens, int lastEstimated)
  {
    if (lastActualPromptTokens is null || lastEstimated <= 0) return estimated;

    var ratio = (double)lastActualPromptTokens.Value / lastEstimated;
    if (ratio is > 0.85 and < 1.15) return estimated;

    return Math.Max((int)(estimated * ratio), 1);
  }
}

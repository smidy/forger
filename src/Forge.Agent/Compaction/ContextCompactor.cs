using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Trace;
using Forge.Core.Types;

namespace Forge.Agent.Compaction;

/// <summary>
/// Dispatches to the strategy named by <see cref="AgentCompactionConfig.Strategy"/>.
/// </summary>
internal static class ContextCompactor
{
  public static async Task<CompactResult> CompactAsync(
    IReadOnlyList<JsonNode> messages,
    AgentCompactionConfig config,
    ToolContext ctx,
    int iteration,
    CancellationToken ct)
  {
    var strategy = config.Strategy?.Trim().ToLowerInvariant() ?? "trim_tool_results";
    return strategy switch
    {
      "trim_tool_results" => await TrimToolResultsStrategy.ExecuteAsync(
        messages, config, ctx, iteration, ct).ConfigureAwait(false),

      "summarise" => await SummariseStrategy.ExecuteAsync(
        messages, config, ctx, iteration, ct).ConfigureAwait(false),

      "window" => await WindowStrategy.ExecuteAsync(
        messages, config, ctx, iteration, ct).ConfigureAwait(false),

      _ => throw new AgentCompactionInvariantException(
        $"Unknown compaction strategy: '{strategy}'. Valid strategies: trim_tool_results, summarise, window.")
    };
  }

  public static ContextCompactedEvent BuildEvent(
    CompactResult result,
    int iteration,
    string strategy) =>
    new()
    {
      Iteration = iteration,
      Strategy = strategy,
      MessagesBefore = result.MessagesBefore,
      MessagesAfter = result.Messages.Count,
      EstimatedTokensBefore = result.EstimatedTokensBefore,
      EstimatedTokensAfter = result.EstimatedTokensAfter,
      CompactedIterations = result.CompactedIterations,
      ArchivePath = result.ArchivePath
    };
}

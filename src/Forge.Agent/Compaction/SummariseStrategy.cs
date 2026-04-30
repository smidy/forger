using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Microsoft.Extensions.Logging;
using static Forge.Agent.Compaction.CompactionWindow;

namespace Forge.Agent.Compaction;

/// <summary>
/// <c>summarise</c> strategy — collapses a compaction-set window into one
/// synthetic <c>assistant</c> + <c>tool</c> pair via an LLM summariser call.
/// Lossier than <c>trim_tool_results</c> but reduces message count (not just
/// per-result size), which matters when the bottleneck is per-message overhead
/// or the agent is getting confused by length rather than byte count.
/// </summary>
internal static class SummariseStrategy
{
  /// <summary>Maximum chars of tool-result content to include in the summariser input preview.</summary>
  private const int ToolContentPreviewChars = 2000;

  public static async Task<CompactResult> ExecuteAsync(
    IReadOnlyList<JsonNode> messages,
    AgentCompactionConfig config,
    ToolContext ctx,
    int iteration,
    CancellationToken ct)
  {
    var partition = CompactionWindow.Partition(messages, config.KeepRecentIterations);

    if (partition.CandidateCount == 0)
    {
      var stable = TokenEstimator.Estimate(messages);
      return new CompactResult
      {
        Messages = messages.ToList(),
        MessagesBefore = messages.Count,
        EstimatedTokensBefore = stable,
        EstimatedTokensAfter = stable,
        CompactedIterations = Array.Empty<int>(),
        ArchivePath = null
      };
    }

    var beforeEstimate = TokenEstimator.Estimate(messages);
    var compactedGroups = CollectCompactionGroups(messages, partition);

    if (compactedGroups.Count == 0)
    {
      return new CompactResult
      {
        Messages = messages.ToList(),
        MessagesBefore = messages.Count,
        EstimatedTokensBefore = beforeEstimate,
        EstimatedTokensAfter = beforeEstimate,
        CompactedIterations = Array.Empty<int>(),
        ArchivePath = null
      };
    }

    // Build the summariser input: serialise each iteration group into a
    // deterministic JSON structure with previewed tool content.
    var summariserInput = BuildSummariserInput(messages, compactedGroups);
    var summariserInputStr = summariserInput.ToJsonString(Forge.Core.Json.JsonSerializationDefaults.CamelCaseTool);

    // Determine the summariser model: SummarisationModel from config, or
    // fall back to "default" which ILlmClient implementations may interpret
    // as the CLI default model. Very few ILlmClient impls exist; the
    // standard path is the caller sets SummarisationModel explicitly.
    var effectiveModel = config.SummarisationModel ?? "default";

    // Call the LLM summariser
    string summary;
    int? summariserPromptTokens = null;
    int? summariserCompletionTokens = null;

    try
    {
      var summariserRequest = new CompletionRequest
      {
        Model = effectiveModel,
        Messages = new List<JsonNode>
        {
          new JsonObject
          {
            ["role"] = "system",
            ["content"] = SummarisePrompt.GetSystemPrompt()
          },
          new JsonObject
          {
            ["role"] = "user",
            ["content"] = summariserInputStr
          }
        },
        MaxTokens = 1024
      };

      var sw = Stopwatch.StartNew();
      var response = await ctx.Llm.CompleteAsync(summariserRequest, ct).ConfigureAwait(false);
      sw.Stop();

      summariserPromptTokens = response.Usage?.PromptTokens;
      summariserCompletionTokens = response.Usage?.CompletionTokens;

      // Emit a separate LlmCallEvent for the summariser call so eval tooling
      // can isolate summariser spend.
      ctx.Trace.Trace(new LlmCallEvent
      {
        Iteration = iteration,
        DurationMs = sw.ElapsedMilliseconds,
        FinishReason = response.Choices?.FirstOrDefault()?.FinishReason,
        PromptTokens = summariserPromptTokens,
        CompletionTokens = summariserCompletionTokens,
        Purpose = "compaction_summarise"
      });

      var choice = response.Choices?.FirstOrDefault();
      summary = choice?.Message?.Content?.GetValue<string>()?.Trim() ?? "";
    }
    catch (Exception ex)
    {
      ctx.Logger.LogWarning(ex, "Summariser LLM call failed for compaction at iteration {Iteration}.", iteration);
      ctx.Trace.Trace(new CompactionSkippedEvent
      {
        Iteration = iteration,
        Reason = "summariser_failed"
      });
      return new CompactResult
      {
        Messages = messages.ToList(),
        MessagesBefore = messages.Count,
        EstimatedTokensBefore = beforeEstimate,
        EstimatedTokensAfter = beforeEstimate,
        CompactedIterations = Array.Empty<int>(),
        ArchivePath = null
      };
    }

    // Check for empty or whitespace-only summary
    if (string.IsNullOrWhiteSpace(summary))
    {
      ctx.Logger.LogWarning("Summariser returned empty/whitespace; skipping compaction at iteration {Iteration}.", iteration);
      ctx.Trace.Trace(new CompactionSkippedEvent
      {
        Iteration = iteration,
        Reason = "summariser_failed"
      });
      return new CompactResult
      {
        Messages = messages.ToList(),
        MessagesBefore = messages.Count,
        EstimatedTokensBefore = beforeEstimate,
        EstimatedTokensAfter = beforeEstimate,
        CompactedIterations = Array.Empty<int>(),
        ArchivePath = null
      };
    }

    // Build the synthetic pair
    var firstIter = compactedGroups[0].IterationIndex;
    var lastIter = compactedGroups[^1].IterationIndex;

    // Build result: preserved head + synthetic pair + preserved tail
    var firstGroupStart = compactedGroups[0].StartIndex;
    var lastGroupEnd = compactedGroups[^1].EndIndex;

    var head = new List<JsonNode>();
    var tail = new List<JsonNode>();
    for (var i = 0; i < messages.Count; i++)
    {
      if (i < firstGroupStart)
        head.Add(messages[i]!.DeepClone());
      else if (i > lastGroupEnd)
        tail.Add(messages[i]!.DeepClone());
    }

    var syntheticAsstMsg = BuildSyntheticAssistantSummary(firstIter, lastIter, summary);
    var syntheticToolMsg = BuildSyntheticToolSummary(firstIter, lastIter, summary, iteration);

    var resultMessages = new List<JsonNode>(head.Count + 2 + tail.Count);
    resultMessages.AddRange(head);
    resultMessages.Add(syntheticAsstMsg);
    resultMessages.Add(syntheticToolMsg);
    resultMessages.AddRange(tail);

    var afterEstimate = TokenEstimator.Estimate(resultMessages);

    // Grow-abort check: if estimated tokens after >= before, abort
    if (afterEstimate >= beforeEstimate)
    {
      ctx.Logger.LogWarning(
        "Summariser compaction would not reduce estimated tokens ({Before} -> {After}); aborting at iteration {Iteration}.",
        beforeEstimate, afterEstimate, iteration);
      ctx.Trace.Trace(new CompactionSkippedEvent
      {
        Iteration = iteration,
        Reason = "summary_exceeds_input"
      });
      return new CompactResult
      {
        Messages = messages.ToList(),
        MessagesBefore = messages.Count,
        EstimatedTokensBefore = beforeEstimate,
        EstimatedTokensAfter = beforeEstimate,
        CompactedIterations = Array.Empty<int>(),
        ArchivePath = null
      };
    }

    // Archive the original compaction-set messages
    var iterationDir = WorkspacePaths.IterationDir(ctx.StageDir, iteration);
    var preCompactionDir = Path.Combine(iterationDir, "pre-compaction");
    Directory.CreateDirectory(preCompactionDir);

    var archiveMessagesPath = Path.Combine(preCompactionDir, "messages.json");
    var archiveMessages = new JsonArray();
    foreach (var grp in compactedGroups)
    {
      for (var i = grp.StartIndex; i <= grp.EndIndex; i++)
      {
        archiveMessages.Add(messages[i]!.DeepClone());
      }
    }
    await File.WriteAllTextAsync(
      archiveMessagesPath,
      archiveMessages.ToJsonString(Forge.Core.Json.JsonSerializationDefaults.Indented),
      Encoding.UTF8,
      ct).ConfigureAwait(false);

    // Also archive full tool outputs separately
    var toolOutputsDir = Path.Combine(preCompactionDir, "tool-outputs");
    Directory.CreateDirectory(toolOutputsDir);
    var outputIdx = 0;
    foreach (var grp in compactedGroups)
    {
      for (var i = grp.StartIndex; i <= grp.EndIndex; i++)
      {
        var role = messages[i]?["role"]?.GetValue<string>() ?? "";
        if (role != "tool") continue;
        var content = messages[i]?["content"]?.GetValue<string>() ?? "{}";
        var toolPath = Path.Combine(toolOutputsDir, $"{outputIdx:D4}.json");
        outputIdx++;
        await File.WriteAllTextAsync(toolPath, content, Encoding.UTF8, ct).ConfigureAwait(false);
      }
    }

    var compactedIterationNumbers = compactedGroups
      .Select(g => g.IterationIndex)
      .ToList();

    return new CompactResult
    {
      Messages = resultMessages,
      MessagesBefore = messages.Count,
      EstimatedTokensBefore = beforeEstimate,
      EstimatedTokensAfter = afterEstimate,
      CompactedIterations = compactedIterationNumbers,
      ArchivePath = preCompactionDir
    };
  }

  /// <summary>
  /// Build the synthetic assistant message that carries the compaction summary.
  /// </summary>
  private static JsonNode BuildSyntheticAssistantSummary(int firstIter, int lastIter, string summary)
  {
    return new JsonObject
    {
      ["role"] = "assistant",
      ["content"] = $"[compacted] Iterations {firstIter}-{lastIter}: {summary}",
      ["tool_calls"] = new JsonArray
      {
        new JsonObject
        {
          ["id"] = $"compaction_{firstIter}_1",
          ["type"] = "function",
          ["function"] = new JsonObject
          {
            ["name"] = PairingInvariant.SyntheticToolName,
            ["arguments"] = "{}"
          }
        }
      }
    };
  }

  /// <summary>
  /// Build the synthetic tool message that pairs with the synthetic assistant.
  /// </summary>
  private static JsonNode BuildSyntheticToolSummary(int firstIter, int lastIter, string summary, int iteration)
  {
    var toolContent = new JsonObject
    {
      ["_compacted"] = true,
      ["iterations"] = new JsonArray { firstIter, lastIter },
      ["summary"] = summary,
      ["archive"] = $"iterations/{iteration:D3}/pre-compaction"
    };

    return new JsonObject
    {
      ["role"] = "tool",
      ["tool_call_id"] = $"compaction_{firstIter}_1",
      ["content"] = toolContent.ToJsonString(Forge.Core.Json.JsonSerializationDefaults.CamelCaseTool)
    };
  }

  /// <summary>
  /// Collect iteration groups within the compaction candidate set.
  /// Each group starts at an assistant message and extends through its
  /// paired tool results.
  /// </summary>
  internal static List<CompactionGroup> CollectCompactionGroups(
    IReadOnlyList<JsonNode> messages,
    CompactionWindowResult partition)
  {
    var groups = new List<CompactionGroup>();
    var iterationIdx = -1;

    for (var i = 2; i < messages.Count; i++)
    {
      var role = messages[i]?["role"]?.GetValue<string>() ?? "";
      if (role == "assistant")
      {
        iterationIdx++;

        if (partition.CandidateIndices.Contains(i))
        {
          var startIdx = i;
          var endIdx = i;

          // Find the end of this group: next assistant that is NOT in the
          // candidate set, or end of list.
          for (var j = i + 1; j < messages.Count; j++)
          {
            var nextRole = messages[j]?["role"]?.GetValue<string>() ?? "";
            if (nextRole == "assistant" && !partition.CandidateIndices.Contains(j))
              break;
            endIdx = j;
          }

          groups.Add(new CompactionGroup
          {
            IterationIndex = iterationIdx,
            StartIndex = startIdx,
            EndIndex = endIdx
          });
        }
      }
    }

    return groups;
  }

  /// <summary>
  /// Build a JSON array summarising each iteration group for the LLM summariser.
  /// Tool-result content is preview-capped to <see cref="ToolContentPreviewChars"/> chars.
  /// </summary>
  internal static JsonArray BuildSummariserInput(
    IReadOnlyList<JsonNode> messages,
    List<CompactionGroup> groups)
  {
    var input = new JsonArray();
    foreach (var grp in groups)
    {
      var groupObj = new JsonObject
      {
        ["iteration"] = grp.IterationIndex
      };

      // Extract assistant message
      var asstMsg = messages[grp.StartIndex];
      groupObj["assistant"] = new JsonObject
      {
        ["content"] = asstMsg?["content"]?.GetValue<string>() ?? "",
        ["reasoning_content"] = asstMsg?["reasoning_content"]?.GetValue<string>(),
        ["tool_calls"] = asstMsg?["tool_calls"]?.DeepClone()
      };

      // Extract tool results
      var toolResults = new JsonArray();
      for (var i = grp.StartIndex + 1; i <= grp.EndIndex; i++)
      {
        var role = messages[i]?["role"]?.GetValue<string>() ?? "";
        if (role != "tool") continue;

        var content = messages[i]?["content"]?.GetValue<string>() ?? "";
        var preview = content.Length <= ToolContentPreviewChars
          ? content
          : content[..ToolContentPreviewChars];

        toolResults.Add(new JsonObject
        {
          ["tool_call_id"] = messages[i]?["tool_call_id"]?.GetValue<string>(),
          ["name"] = ResolveToolName(messages, i),
          ["preview"] = preview
        });
      }
      groupObj["tool_results"] = toolResults;

      // User nudge (if any) follows the tool results
      if (grp.EndIndex + 1 < messages.Count)
      {
        var nextRole = messages[grp.EndIndex + 1]?["role"]?.GetValue<string>() ?? "";
        if (nextRole == "user")
        {
          groupObj["user_nudge"] = messages[grp.EndIndex + 1]?["content"]?.GetValue<string>() ?? "";
        }
      }

      input.Add(groupObj);
    }

    return input;
  }

  /// <summary>
  /// Resolve the tool name from an assistant message that declared a tool_call
  /// matching the given tool message's <c>tool_call_id</c>.
  /// </summary>
  private static string ResolveToolName(IReadOnlyList<JsonNode> messages, int toolMsgIndex)
  {
    var toolCallId = messages[toolMsgIndex]?["tool_call_id"]?.GetValue<string>();
    if (toolCallId is null) return "unknown";

    // Walk back to find the matching assistant
    for (var i = toolMsgIndex - 1; i >= 0; i--)
    {
      var role = messages[i]?["role"]?.GetValue<string>() ?? "";
      if (role != "assistant") continue;
      if (messages[i]?["tool_calls"] is not JsonArray calls) continue;

      foreach (var call in calls)
      {
        if (call?["id"]?.GetValue<string>() == toolCallId)
        {
          return call?["function"]?["name"]?.GetValue<string>() ?? "unknown";
        }
      }
    }

    return "unknown";
  }

  /// <summary>Internal group descriptor used during summariser processing.</summary>
  internal sealed class CompactionGroup
  {
    public required int IterationIndex { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
  }
}

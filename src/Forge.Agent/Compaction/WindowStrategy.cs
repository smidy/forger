using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Types;
using Forge.Core.Workspace;
using static Forge.Agent.Compaction.CompactionWindow;

namespace Forge.Agent.Compaction;

/// <summary>
/// <c>window</c> strategy — hard-drops whole iteration groups from the
/// compaction set. Simplest; atomic at the group boundary so pairing stays
/// valid. Useful when an agent's early iterations are provably stale
/// (e.g. exploration turns before a decision).
///
/// Two knobs:
/// <list type="bullet">
///   <item><c>drop_oldest_iterations</c> (default <c>null</c> → drop all)</item>
///   <item><c>keep_recent_iterations</c> (shared with parent plan; unchanged)</item>
/// </list>
/// </summary>
internal static class WindowStrategy
{
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
    var groups = CollectWindowGroups(messages, partition);

    if (groups.Count == 0)
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

    // Determine how many groups to drop
    var dropCount = config.DropOldestIterations ?? groups.Count;
    if (dropCount > groups.Count) dropCount = groups.Count;
    if (dropCount <= 0)
    {
      // Nothing to drop
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

    var droppedGroups = groups.Take(dropCount).ToList();
    var keptGroups = groups.Skip(dropCount).ToList();

    if (droppedGroups.Count == 0)
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

    // Build the result: preserved head + kept groups + preserved tail
    var firstDropStart = droppedGroups[0].StartIndex;
    var lastDropEnd = droppedGroups[^1].EndIndex;

    var resultMessages = new List<JsonNode>();

    // Add all messages before the first dropped group
    for (var i = 0; i < firstDropStart; i++)
    {
      resultMessages.Add(messages[i]!.DeepClone());
    }

    // Add the kept groups (if any)
    foreach (var kept in keptGroups)
    {
      for (var i = kept.StartIndex; i <= kept.EndIndex; i++)
      {
        resultMessages.Add(messages[i]!.DeepClone());
      }
    }

    var compactionWindowEnd = keptGroups.Count > 0
      ? keptGroups[^1].EndIndex
      : lastDropEnd;
    for (var i = compactionWindowEnd + 1; i < messages.Count; i++)
    {
      resultMessages.Add(messages[i]!.DeepClone());
    }

    var afterEstimate = TokenEstimator.Estimate(resultMessages);

    // Archive dropped groups
    var iterationDir = WorkspacePaths.IterationDir(ctx.StageDir, iteration);
    var preCompactionDir = Path.Combine(iterationDir, "pre-compaction");
    Directory.CreateDirectory(preCompactionDir);

    var droppedMessagesPath = Path.Combine(preCompactionDir, "dropped-messages.json");
    var droppedMsgs = new JsonArray();
    foreach (var grp in droppedGroups)
    {
      for (var i = grp.StartIndex; i <= grp.EndIndex; i++)
      {
        droppedMsgs.Add(messages[i]!.DeepClone());
      }
    }
    await File.WriteAllTextAsync(
      droppedMessagesPath,
      droppedMsgs.ToJsonString(Forge.Core.Json.JsonSerializationDefaults.Indented),
      Encoding.UTF8,
      ct).ConfigureAwait(false);

    // Also archive per-tool-output originals for eval tooling
    var toolOutputsDir = Path.Combine(preCompactionDir, "tool-outputs");
    Directory.CreateDirectory(toolOutputsDir);
    var outputIdx = 0;
    foreach (var grp in droppedGroups)
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

    var droppedIterationNumbers = droppedGroups
      .Select(g => g.IterationIndex)
      .ToList();

    return new CompactResult
    {
      Messages = resultMessages,
      MessagesBefore = messages.Count,
      EstimatedTokensBefore = beforeEstimate,
      EstimatedTokensAfter = afterEstimate,
      CompactedIterations = droppedIterationNumbers,
      ArchivePath = preCompactionDir
    };
  }

  /// <summary>
  /// Collect iteration groups within the compaction candidate set, in order.
  /// </summary>
  internal static List<WindowGroup> CollectWindowGroups(
    IReadOnlyList<JsonNode> messages,
    CompactionWindowResult partition)
  {
    var groups = new List<WindowGroup>();
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

          for (var j = i + 1; j < messages.Count; j++)
          {
            var nextRole = messages[j]?["role"]?.GetValue<string>() ?? "";
            if (nextRole == "assistant" || !partition.CandidateIndices.Contains(j))
              break;
            endIdx = j;
          }

          groups.Add(new WindowGroup
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

  internal sealed class WindowGroup
  {
    public required int IterationIndex { get; init; }
    public required int StartIndex { get; init; }
    public required int EndIndex { get; init; }
  }
}

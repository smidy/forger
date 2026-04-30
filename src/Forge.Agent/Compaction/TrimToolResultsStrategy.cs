using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Types;
using Forge.Core.Workspace;
using static Forge.Agent.Compaction.CompactionWindow;

namespace Forge.Agent.Compaction;

/// <summary>
/// Rewrites stale <c>tool</c> message content into disk-backed stubs in the
/// same format as <see cref="ToolResultCapper"/>.
/// </summary>
internal static class TrimToolResultsStrategy
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
    var compactedIterations = CollectCompactedIterations(messages, partition);

    // Archive dir: iterations/NNN/pre-compaction/tool-outputs/ under the stage.
    var iterationDir = WorkspacePaths.IterationDir(ctx.StageDir, iteration);
    var preCompactionAbsDir = Path.Combine(iterationDir, "pre-compaction", "tool-outputs");
    var preCompactionRelDir = Path.GetRelativePath(ctx.StageDir, preCompactionAbsDir).Replace('\\', '/');
    Directory.CreateDirectory(preCompactionAbsDir);

    var resultMessages = new List<JsonNode>(messages.Count);
    var pendingWrites = new List<(string Path, string Content)>();
    var outputIdx = 0;

    for (var idx = 0; idx < messages.Count; idx++)
    {
      var msg = messages[idx];
      if (partition.PreservedIndices.Contains(idx))
      {
        // Preserved messages pass through by reference — the caller replaces
        // the messages list atomically, so no parent-conflict risk.
        resultMessages.Add(msg!);
        continue;
      }

      if (!partition.CandidateIndices.Contains(idx))
      {
        continue;
      }

      var role = msg?["role"]?.GetValue<string>() ?? "";
      if (role != "tool")
      {
        resultMessages.Add(msg!);
        continue;
      }

      var content = msg?["content"]?.GetValue<string>();
      if (content is not null && TryParseJson(content) is JsonObject existing
        && existing["_truncated"]?.GetValue<bool>() == true)
      {
        resultMessages.Add(msg!);
        continue;
      }

      var archiveIdx = outputIdx++;
      var archiveFileName = $"{archiveIdx:D4}.json";
      var archiveAbsPath = Path.Combine(preCompactionAbsDir, archiveFileName);
      var archiveRelPath = $"{preCompactionRelDir}/{archiveFileName}";
      var bodyToArchive = content ?? "{}";

      pendingWrites.Add((archiveAbsPath, bodyToArchive));

      var sizeBytes = Encoding.UTF8.GetByteCount(bodyToArchive);
      var stub = ToolResultCapper.BuildStubPayload(bodyToArchive, sizeBytes, archiveRelPath);
      stub["_compacted_at_iteration"] = iteration;

      resultMessages.Add(new JsonObject
      {
        ["role"] = "tool",
        ["tool_call_id"] = msg!["tool_call_id"]?.DeepClone(),
        ["content"] = stub.ToJsonString()
      });
    }

    if (pendingWrites.Count > 0)
    {
      await Task.WhenAll(pendingWrites.Select(w =>
        File.WriteAllTextAsync(w.Path, w.Content, Encoding.UTF8, ct))).ConfigureAwait(false);
    }

    var afterEstimate = TokenEstimator.Estimate(resultMessages);

    return new CompactResult
    {
      Messages = resultMessages,
      MessagesBefore = messages.Count,
      EstimatedTokensBefore = beforeEstimate,
      EstimatedTokensAfter = afterEstimate,
      CompactedIterations = compactedIterations,
      ArchivePath = preCompactionAbsDir
    };
  }

  private static IReadOnlyList<int> CollectCompactedIterations(
    IReadOnlyList<JsonNode> messages,
    CompactionWindowResult partition)
  {
    var iters = new List<int>();
    var asstIdx = 0;
    for (var i = 2; i < messages.Count; i++)
    {
      var role = messages[i]?["role"]?.GetValue<string>() ?? "";
      if (role != "assistant") continue;
      if (partition.CandidateIndices.Contains(i)) iters.Add(asstIdx);
      asstIdx++;
    }
    return iters;
  }

  private static JsonNode? TryParseJson(string content)
  {
    try { return JsonNode.Parse(content); }
    catch (JsonException) { return null; }
  }
}

using System.Text.Json.Nodes;

namespace Forge.Agent.Compaction;

/// <summary>
/// Partitions the messages list into indices that must be preserved verbatim
/// and indices that are compaction candidates. Operates on iteration groups
/// where each group is {assistant, zero-or-more tool results, optional user nudge}.
/// </summary>
internal static class CompactionWindow
{
  public static CompactionWindowResult Partition(
    IReadOnlyList<JsonNode> messages,
    int keepRecent)
  {
    var preservedIndices = new HashSet<int>();

    if (messages.Count >= 1) preservedIndices.Add(0);
    if (messages.Count >= 2) preservedIndices.Add(1);

    var groupStarts = new List<int>();
    for (var i = 2; i < messages.Count; i++)
    {
      if (GetRole(messages[i]) == "assistant") groupStarts.Add(i);
    }

    if (groupStarts.Count == 0)
    {
      for (var i = 2; i < messages.Count; i++) preservedIndices.Add(i);
      return new CompactionWindowResult
      {
        CandidateCount = 0,
        PreservedIndices = preservedIndices,
        CandidateIndices = new HashSet<int>()
      };
    }

    // Preserve the last N assistant groups (each group extends to the next
    // assistant or the end of the list).
    var preserveCount = Math.Min(keepRecent, groupStarts.Count);
    for (var gi = groupStarts.Count - preserveCount; gi < groupStarts.Count; gi++)
    {
      var startIdx = groupStarts[gi];
      var endIdx = (gi + 1 < groupStarts.Count) ? groupStarts[gi + 1] : messages.Count;
      for (var i = startIdx; i < endIdx; i++) preservedIndices.Add(i);
    }

    var candidateIndices = new HashSet<int>();
    for (var i = 2; i < messages.Count; i++)
    {
      if (!preservedIndices.Contains(i)) candidateIndices.Add(i);
    }

    // Pairing guard: if a preserved assistant references a tool_call_id whose
    // tool result is in the candidate set, pull that tool result back. Rare but
    // required — without this, compaction would orphan a live tool-call id.
    var preservedToolCallIds = CollectPreservedToolCallIds(messages, preservedIndices);
    if (preservedToolCallIds.Count > 0)
    {
      PromoteReferencedToolResults(messages, preservedToolCallIds, preservedIndices, candidateIndices);
    }

    return new CompactionWindowResult
    {
      CandidateCount = candidateIndices.Count,
      PreservedIndices = preservedIndices,
      CandidateIndices = candidateIndices
    };
  }

  private static HashSet<string> CollectPreservedToolCallIds(
    IReadOnlyList<JsonNode> messages, HashSet<int> preservedIndices)
  {
    var ids = new HashSet<string>();
    foreach (var idx in preservedIndices)
    {
      if (GetRole(messages[idx]) != "assistant") continue;
      if (messages[idx]?["tool_calls"] is not JsonArray calls) continue;
      foreach (var call in calls)
      {
        var id = call?["id"]?.GetValue<string>();
        if (id is not null) ids.Add(id);
      }
    }
    return ids;
  }

  private static void PromoteReferencedToolResults(
    IReadOnlyList<JsonNode> messages,
    HashSet<string> preservedToolCallIds,
    HashSet<int> preservedIndices,
    HashSet<int> candidateIndices)
  {
    var promoted = new List<int>();
    foreach (var idx in candidateIndices)
    {
      if (GetRole(messages[idx]) != "tool") continue;
      var toolCallId = messages[idx]?["tool_call_id"]?.GetValue<string>();
      if (toolCallId is not null && preservedToolCallIds.Contains(toolCallId)) promoted.Add(idx);
    }
    foreach (var idx in promoted)
    {
      preservedIndices.Add(idx);
      candidateIndices.Remove(idx);
    }
  }

  private static string GetRole(JsonNode? node) => node?["role"]?.GetValue<string>() ?? "";

  internal sealed class CompactionWindowResult
  {
    public required int CandidateCount { get; init; }
    public required HashSet<int> PreservedIndices { get; init; }
    public required HashSet<int> CandidateIndices { get; init; }
  }
}

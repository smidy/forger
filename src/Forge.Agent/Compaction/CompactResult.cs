using System.Text.Json.Nodes;

namespace Forge.Agent.Compaction;

/// <summary>Output of a compaction pass. Strategy-agnostic.</summary>
internal sealed class CompactResult
{
  public required List<JsonNode> Messages { get; init; }
  public required int MessagesBefore { get; init; }
  public required int EstimatedTokensBefore { get; init; }
  public required int EstimatedTokensAfter { get; init; }
  public required IReadOnlyList<int> CompactedIterations { get; init; }

  /// <summary>Absolute path to the pre-compaction archive dir. Null when no compaction occurred.</summary>
  public required string? ArchivePath { get; init; }
}

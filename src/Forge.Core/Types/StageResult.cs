using System.Text.Json.Nodes;

namespace Forge.Core.Types;

public sealed class StageResult
{
  public required JsonNode Output { get; init; }
}

using System.Text.Json.Nodes;

namespace Forge.Core.Types;

public sealed class RuntimeContext
{
  public string RunId { get; init; } = "";
  public string Workspace { get; init; } = "";
  public string StageDir { get; init; } = "";
  public JsonNode? FanOutItem { get; init; }
  public int? FanOutIndex { get; init; }
}

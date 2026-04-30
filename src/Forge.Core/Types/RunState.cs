using System.Text.Json.Nodes;

namespace Forge.Core.Types;

public sealed class RunState
{
  public required JsonNode Input { get; set; }
  public Dictionary<string, StageResult> Stages { get; } = new(StringComparer.Ordinal);
  public Dictionary<string, AgentResumeState> PendingAgentResume { get; } = new(StringComparer.Ordinal);
  public required RuntimeContext Runtime { get; set; }

  /// <summary>Unified JSON document for JSONPath and templates (runtime keys use snake_case per DSL).</summary>
  public JsonNode AsStateJson()
  {
    var stages = new JsonObject();
    foreach (var kv in Stages)
    {
      stages[kv.Key] = new JsonObject { ["output"] = kv.Value.Output.DeepClone() };
    }

    var runtime = new JsonObject
    {
      ["run_id"] = Runtime.RunId,
      ["workspace"] = Runtime.Workspace,
      ["stage_dir"] = Runtime.StageDir
    };
    if (Runtime.FanOutItem is not null)
    {
      runtime["item"] = Runtime.FanOutItem.DeepClone();
    }

    if (Runtime.FanOutIndex is not null)
    {
      runtime["index"] = JsonValue.Create(Runtime.FanOutIndex.Value);
    }

    return new JsonObject
    {
      ["input"] = Input.DeepClone(),
      ["stages"] = stages,
      ["runtime"] = runtime
    };
  }
}

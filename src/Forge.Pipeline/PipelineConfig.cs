using System.Text.Json.Nodes;
using Forge.Agent;
using Forge.Core.Json;

namespace Forge.Pipeline;

public sealed class PipelineConfig
{
  public required string Name { get; init; }
  public string Version { get; init; } = "1";
  public required List<StageConfig> Stages { get; init; }
  public JsonNode? Filesystem { get; init; }
  public JsonNode? Outputs { get; init; }
  public JsonNode? OutputSchema { get; init; }

  public static PipelineConfig FromJsonNode(JsonNode node)
  {
    var o = node.AsObject();
    var stages = new List<StageConfig>();
    if (o["stages"] is JsonArray arr)
    {
      foreach (var s in arr)
      {
        if (s is JsonObject so)
        {
          stages.Add(StageConfig.FromJsonObject(so));
        }
      }
    }

    return new PipelineConfig
    {
      Name = JsonNodeHelpers.Str(o["name"]),
      Version = JsonNodeHelpers.Str(o["version"]),
      Stages = stages,
      Filesystem = o["filesystem"]?.DeepClone(),
      Outputs = o["outputs"]?.DeepClone(),
      OutputSchema = o["output_schema"]?.DeepClone()
    };
  }

  public static PipelineConfig LoadYamlFile(string path)
  {
    var text = File.ReadAllText(path);
    var json = YamlFront.ParseToJson(text);
    return FromJsonNode(json);
  }
}

public sealed class StageConfig
{
  public required string Id { get; init; }
  public List<string> DependsOn { get; init; } = new();
  public string? Agent { get; init; }
  public string? Tool { get; init; }
  public JsonNode? Input { get; init; }
  public JsonNode? Filesystem { get; init; }
  public string? FanOut { get; init; }
  public int? Concurrency { get; init; }

  public string OnError { get; init; } = "fail";

  public bool ContinueOnError => string.Equals(OnError, "continue", StringComparison.OrdinalIgnoreCase);

  public static StageConfig FromJsonObject(JsonObject o) =>
    new()
    {
      Id = JsonNodeHelpers.Str(o["id"]),
      DependsOn = JsonNodeHelpers.ListStr(o["depends_on"]),
      Agent = JsonNodeHelpers.NullableStr(o["agent"]),
      Tool = JsonNodeHelpers.NullableStr(o["tool"]),
      Input = o["input"]?.DeepClone(),
      Filesystem = o["filesystem"]?.DeepClone(),
      FanOut = JsonNodeHelpers.NullableStr(o["fan_out"]),
      Concurrency = JsonNodeHelpers.Int(o["concurrency"]),
      OnError = JsonNodeHelpers.NullableStr(o["on_error"]) ?? "fail"
    };
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Json.Schema;

namespace Forge.Core.Schema;

public static class Validator
{
  /// <summary>Validates <paramref name="value"/> against <paramref name="schema"/> (JsonSchema.Net / json-everything).</summary>
  public static void Validate(JsonNode value, JsonSchema schema)
  {
    using var doc = JsonDocument.Parse(value.ToJsonString());
    var result = schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (result.IsValid)
    {
      return;
    }

    var path = "$";
    var message = "Schema validation failed.";
    var first = FindFirstFailure(result);
    if (first is not null)
    {
      path = first.InstanceLocation.ToString();
      var err = first.Errors is { } e ? e.Values.FirstOrDefault() : null;
      message = err is not null
        ? $"{err} (path: {first.EvaluationPath}, schema: {first.SchemaLocation})"
        : $"Schema validation failed: {first.EvaluationPath} ({first.SchemaLocation})";
    }

    throw new ValidationException(message, path);
  }

  /// <summary>Depth-first: prefer a node that recorded keyword <see cref="EvaluationResults.Errors"/>.</summary>
  private static EvaluationResults? FindFirstFailure(EvaluationResults node)
  {
    foreach (var child in node.Details ?? [])
    {
      var hit = FindFirstFailure(child);
      if (hit is not null)
      {
        return hit;
      }
    }

    if (!node.IsValid && node.Errors is { Count: > 0 })
    {
      return node;
    }

    return !node.IsValid ? node : null;
  }
}

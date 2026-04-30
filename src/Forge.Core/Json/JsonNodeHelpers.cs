using System.Text.Json.Nodes;

namespace Forge.Core.Json;

public static class JsonNodeHelpers
{
  public static string Str(JsonNode? n) =>
    n is JsonValue v && v.TryGetValue(out string? s) ? s : "";

  public static string? NullableStr(JsonNode? n) =>
    n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

  public static int? Int(JsonNode? n)
  {
    if (n is not JsonValue v)
    {
      return null;
    }

    // Direct int match (System.Text.Json-native JsonValue.Create(42)).
    if (v.TryGetValue(out int i))
    {
      return i;
    }

    // YamlDotNet / Yaml2JsonNode stores YAML integers as `decimal`; fall through
    // to `decimal` and `long` so YAML-sourced values aren't silently dropped.
    if (v.TryGetValue(out long l))
    {
      return l >= int.MinValue && l <= int.MaxValue ? (int)l : null;
    }

    if (v.TryGetValue(out decimal d) && d == decimal.Truncate(d) && d >= int.MinValue && d <= int.MaxValue)
    {
      return (int)d;
    }

    return null;
  }

  public static bool Bool(JsonNode? n, bool defaultValue) =>
    n is JsonValue v && v.TryGetValue(out bool b) ? b : defaultValue;

  public static List<string> ListStr(JsonNode? n)
  {
    if (n is not JsonArray a)
    {
      return new List<string>();
    }

    return a.Select(x => x is JsonValue j && j.TryGetValue(out string? s) ? s : "")
      .Where(s => !string.IsNullOrEmpty(s))
      .ToList();
  }
}

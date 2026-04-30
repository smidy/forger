using System.Text.Json.Nodes;
using Forge.Core.Types;
using Json.Path;
using Scriban;
using Scriban.Runtime;

namespace Forge.Core.Refs;

public sealed class Resolver
{
  public static bool IsReference(string s) => StringClassifier.IsReference(s);

  public JsonNode? ResolveRef(string expr, RunState state)
  {
    var root = state.AsStateJson();
    if (string.Equals(expr, "$item", StringComparison.Ordinal))
    {
      return state.Runtime.FanOutItem?.DeepClone();
    }

    if (expr.StartsWith("$item.", StringComparison.Ordinal))
    {
      var sub = expr["$item.".Length..];
      JsonNode item = state.Runtime.FanOutItem ?? new JsonObject();
      return EvaluateSubPath(item, sub);
    }

    if (string.Equals(expr, "$index", StringComparison.Ordinal))
    {
      return state.Runtime.FanOutIndex is int i ? JsonValue.Create(i) : null;
    }

    if (expr.StartsWith("$index.", StringComparison.Ordinal))
    {
      var idx = state.Runtime.FanOutIndex;
      var fake = idx is int v ? JsonValue.Create(v) : JsonValue.Create((int?)null);
      return EvaluateSubPath(fake!, expr["$index.".Length..]);
    }

    if (!expr.StartsWith("$.", StringComparison.Ordinal))
    {
      throw new InvalidOperationException($"Not a JSONPath reference: {expr}");
    }

    return EvaluateJsonPath(expr, root);
  }

  private static JsonNode? EvaluateSubPath(JsonNode root, string jsonPathSuffix)
  {
    if (string.IsNullOrEmpty(jsonPathSuffix))
    {
      return root.DeepClone();
    }

    var expr = "$" + (jsonPathSuffix.StartsWith('.') ? jsonPathSuffix : "." + jsonPathSuffix);
    return EvaluateJsonPath(expr, root);
  }

  private static JsonNode? EvaluateJsonPath(string expr, JsonNode root)
  {
    var path = JsonPath.Parse(expr);
    var result = path.Evaluate(root);
    // See https://docs.json-everything.net/path/basics/ — Evaluate returns a result with a deferred nodelist (IEnumerable<Node>).
    if (result.Matches is null || !result.Matches.Any())
    {
      return null;
    }

    return FlattenMatches(result.Matches);
  }

  private static JsonNode FlattenMatches(IEnumerable<Node> matches)
  {
    var list = new JsonArray();
    foreach (var m in matches)
    {
      if (m.Value is JsonNode jn)
      {
        list.Add(jn.DeepClone());
      }
    }

    return list;
  }

  public JsonNode ResolveDeep(JsonNode obj, RunState state)
  {
    return ResolveDeepCore(obj, state) ?? JsonValue.Create((string?)null)!;
  }

  private JsonNode? ResolveDeepCore(JsonNode? node, RunState state)
  {
    switch (node)
    {
      case JsonValue jv when jv.TryGetValue(out string? str):
        return ResolveString(str, state);
      case JsonObject o:
      {
        var copy = new JsonObject();
        foreach (var p in o)
        {
          copy[p.Key] = ResolveDeepCore(p.Value, state) ?? JsonValue.Create((string?)null)!;
        }

        return copy;
      }
      case JsonArray a:
      {
        var copy = new JsonArray();
        foreach (var item in a)
        {
          copy.Add(ResolveDeepCore(item, state) ?? JsonValue.Create((string?)null)!);
        }

        return copy;
      }
      default:
        return node?.DeepClone();
    }
  }

  private JsonNode ResolveString(string str, RunState state)
  {
    switch (StringClassifier.Classify(str))
    {
      case StringClassification.Literal:
        return JsonValue.Create(StringClassifier.UnescapeLiteral(str))!;
      case StringClassification.Reference:
        return ResolveRef(str, state)?.DeepClone() ?? JsonValue.Create((string?)null)!;
      case StringClassification.Template:
        return JsonValue.Create(ResolveTemplate(str, state))!;
      default:
        throw new InvalidOperationException();
    }
  }

  public string ResolveTemplate(string str, RunState state) =>
    ResolveTemplate(str, state, extend: null);

  /// <summary>
  /// Render a Scriban template against <paramref name="state"/>, optionally
  /// mutating the global <see cref="ScriptObject"/> just before parse/render so
  /// callers can inject additional members (e.g. a <c>forge</c> namespace with
  /// runtime values like <c>today</c>). The extender runs after the state-derived
  /// members are populated, so it can add new names or override them — callers
  /// are responsible for avoiding accidental shadowing of input members.
  /// </summary>
  public string ResolveTemplate(string str, RunState state, Action<ScriptObject>? extend)
  {
    var root = state.AsStateJson();
    var so = JsonToScriptObject(root) as ScriptObject ?? new ScriptObject();
    extend?.Invoke(so);
    var tpl = Template.Parse(str);
    var ctx = new TemplateContext { StrictVariables = true };
    ctx.PushGlobal(so);
    return tpl.Render(ctx);
  }

  private static object? JsonToScriptObject(JsonNode? node)
  {
    return node switch
    {
      JsonObject o => ObjectFromJson(o),
      JsonArray a => a.Select(n => JsonToScriptObject(n)).ToList(),
      JsonValue v => ExtractScalar(v),
      null => null,
      _ => node.ToJsonString()
    };
  }

  private static ScriptObject ObjectFromJson(JsonObject o)
  {
    var so = new ScriptObject();
    foreach (var p in o)
    {
      so[p.Key] = JsonToScriptObject(p.Value);
    }

    return so;
  }

  private static object? ExtractScalar(JsonValue v)
  {
    if (v.TryGetValue(out string? s))
    {
      return s;
    }

    if (v.TryGetValue(out int i))
    {
      return i;
    }

    if (v.TryGetValue(out long l))
    {
      return l;
    }

    if (v.TryGetValue(out double d))
    {
      return d;
    }

    if (v.TryGetValue(out bool b))
    {
      return b;
    }

    return v.ToJsonString();
  }
}

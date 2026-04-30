using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Json;
using Forge.Core.Schema;
using Forge.Core.Types;
using Json.Schema;

namespace Forge.Tools;

public abstract class ToolBase<TIn, TOut> : ITool
{
  private static readonly JsonSerializerOptions JsonOpts = JsonSerializationDefaults.CamelCaseTool;

  public abstract string Name { get; }
  public abstract string Description { get; }
  public JsonSchema InputSchema => SchemaExporter.GetSchema<TIn>();
  public JsonSchema OutputSchema => SchemaExporter.GetSchema<TOut>();

  public async Task<JsonNode> ExecuteAsync(JsonNode input, ToolContext ctx, CancellationToken cancellationToken)
  {
    Validator.Validate(input, InputSchema);
    TIn model;
    try
    {
      model = JsonSerializer.Deserialize<TIn>(input.ToJsonString(), JsonOpts)!;
    }
    catch (JsonException ex)
    {
      throw new ValidationException($"Invalid tool input: {ex.Message}");
    }

    var output = await ExecuteCoreAsync(model, ctx, cancellationToken).ConfigureAwait(false);
    return JsonSerializer.SerializeToNode(output, JsonOpts)!;
  }

  public virtual string? SummarizeCall(TIn input, TOut? output, string? error) => null;

  public string TrySummarize(JsonNode input, JsonNode? output, string? error)
  {
    TIn? typedInput;
    try
    {
      typedInput = input.Deserialize<TIn>(JsonOpts);
    }
    catch
    {
      typedInput = default;
    }

    TOut? typedOutput = default;
    if (output is not null)
    {
      try
      {
        typedOutput = output.Deserialize<TOut>(JsonOpts);
      }
      catch
      {
        typedOutput = default;
      }
    }

    var custom = typedInput is not null ? SummarizeCall(typedInput, typedOutput, error) : null;
    if (custom is not null)
    {
      return custom;
    }

    var firstStr = FirstStringField(input);
    if (error is not null)
    {
      var errTrunc = Truncate(error);
      return firstStr is not null
        ? $"{firstStr} → error: {errTrunc}"
        : $"error: {errTrunc}";
    }

    var outBytes = output is not null
      ? System.Text.Encoding.UTF8.GetByteCount(output.ToJsonString())
      : 0;

    return firstStr is not null
      ? $"{firstStr} → {outBytes} bytes"
      : $"{outBytes} bytes";
  }

  protected static string Truncate(string s, int max = 60) =>
    s.Length > max ? s[..max] + "…" : s;

  private static string? FirstStringField(JsonNode node)
  {
    if (node is not JsonObject obj)
    {
      return null;
    }

    foreach (var kv in obj)
    {
      if (kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
      {
        return Truncate(s);
      }
    }

    return null;
  }

  protected abstract Task<TOut> ExecuteCoreAsync(TIn input, ToolContext ctx, CancellationToken cancellationToken);
}

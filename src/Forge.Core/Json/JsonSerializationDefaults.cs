using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Forge.Core.Json;

/// <summary>.NET 8+ requires an explicit <see cref="JsonSerializerOptions.TypeInfoResolver"/> for reflection-based serialization.</summary>
/// <remarks>
/// The tool-facing options use <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so
/// characters like <c>&lt;</c>, <c>&gt;</c>, <c>+</c>, and <c>&amp;</c> round-trip as themselves
/// instead of being escaped as <c>\u003C</c> / <c>\u003E</c> / <c>\u002B</c> / <c>\u0026</c>.
/// The content is embedded as a string inside a chat-completions <c>content</c> field, so the
/// LLM reads it as literal text — HTML-safe escaping caused <c>apply_patch</c> rejects when
/// the model echoed back <c>\u003C</c> sequences that no longer matched the on-disk file.
/// </remarks>
public static class JsonSerializationDefaults
{
  public static JsonSerializerOptions CamelCaseTool { get; } = CreateCamelCase();
  public static JsonSerializerOptions Indented { get; } = CreateIndented();
  public static JsonSerializerOptions Trace { get; } = CreateTrace();
  public static JsonSerializerOptions LiteLlmConfig { get; } = CreateLiteLlmConfig();

  public static JsonSerializerOptions General { get; } = new JsonSerializerOptions(JsonSerializerDefaults.General)
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  private static JsonSerializerOptions CreateCamelCase() =>
    new()
    {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

  private static JsonSerializerOptions CreateIndented() =>
    new()
    {
      WriteIndented = true,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

  private static JsonSerializerOptions CreateTrace() =>
    new()
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = false,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

  private static JsonSerializerOptions CreateLiteLlmConfig() =>
    new()
    {
      PropertyNameCaseInsensitive = true,
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}

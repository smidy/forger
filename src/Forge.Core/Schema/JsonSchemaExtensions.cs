using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;

namespace Forge.Core.Schema;

public static class JsonSchemaExtensions
{
  private static readonly JsonSerializerOptions SerializerOptions = Create();

  private static JsonSerializerOptions Create()
  {
    var o = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    o.Converters.Add(new JsonSchemaJsonConverter());
    return o;
  }

  public static JsonNode ToJsonNode(this JsonSchema schema)
  {
    var json = JsonSerializer.Serialize(schema, SerializerOptions);
    return JsonNode.Parse(json)!;
  }
}

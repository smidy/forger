using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Forge.Core.Json;
using Json.Schema;

namespace Forge.Core.Schema;

public static class SchemaExporter
{
  public static JsonSchema GetSchema<T>(JsonSerializerOptions? options = null)
  {
    var opt = options ?? JsonSerializationDefaults.General;
    // Root-level reference types must be non-nullable so tool schemas emit
    // type: "object" rather than type: ["object","null"] — strict OpenAI-schema
    // validators (e.g. DeepSeek) reject union types on function parameters.
    var exportOpts = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true };
    var node = JsonSchemaExporter.GetJsonSchemaAsNode(opt, typeof(T), exportOpts);
    return JsonSchema.FromText(node.ToJsonString());
  }
}

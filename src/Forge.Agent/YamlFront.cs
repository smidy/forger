using System.Text;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace Forge.Agent;

public static class YamlFront
{
  public static System.Text.Json.Nodes.JsonNode ParseToJson(string yamlText)
  {
    using var reader = new StringReader(yamlText);
    var yaml = new YamlStream();
    yaml.Load(reader);
    if (yaml.Documents.Count == 0)
    {
      return new System.Text.Json.Nodes.JsonObject();
    }

    return YamlConverter.ToJsonNode(yaml.Documents[0]) ?? new System.Text.Json.Nodes.JsonObject();
  }
}

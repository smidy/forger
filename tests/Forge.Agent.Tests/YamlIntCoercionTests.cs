using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Json;

namespace Forge.Agent.Tests;

public class YamlIntCoercionTests
{
  [Fact]
  public void Yaml_integer_is_readable_via_JsonNodeHelpers_Int()
  {
    var yaml = "max_iterations: 48\n";
    var node = YamlFront.ParseToJson(yaml);
    var obj = node.AsObject();
    JsonNodeHelpers.Int(obj["max_iterations"]).Should().Be(48);
  }

  [Fact]
  public void Yaml_integer_round_trips_through_AgentConfig()
  {
    var yaml = """
      name: test
      model: test-model
      max_iterations: 48
      system_prompt: "s"
      user_prompt: "u"
      input_schema: {type: object}
      output_schema: {type: object}
      """;
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.MaxIterations.Should().Be(48);
  }
}

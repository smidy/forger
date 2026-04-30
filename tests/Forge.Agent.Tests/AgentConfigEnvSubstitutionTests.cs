using FluentAssertions;
using Forge.Agent;

namespace Forge.Agent.Tests;

public class AgentConfigEnvSubstitutionTests
{
  [Fact]
  public void Model_field_expands_env_var_at_load_time()
  {
    const string Var = "FORGE_EVAL_TEST_MODEL_X";
    Environment.SetEnvironmentVariable(Var, "resolved-model-id");
    try
    {
      var yaml = """
        name: test
        model: ${FORGE_EVAL_TEST_MODEL_X}
        system_prompt: "s"
        user_prompt: "u"
        input_schema: {type: object}
        output_schema: {type: object}
        """;
      var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
      cfg.Model.Should().Be("resolved-model-id");
    }
    finally
    {
      Environment.SetEnvironmentVariable(Var, null);
    }
  }

  [Fact]
  public void Model_field_expands_missing_env_var_to_empty()
  {
    // Deliberate sanity check: without the variable set, expansion returns empty
    // rather than leaving literal ${…}, so downstream code can detect "unset".
    var yaml = """
      name: test
      model: ${FORGE_EVAL_DEFINITELY_NOT_SET_ABCXYZ}
      system_prompt: "s"
      user_prompt: "u"
      input_schema: {type: object}
      output_schema: {type: object}
      """;
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Model.Should().BeEmpty();
  }
}

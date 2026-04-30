using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;

namespace Forge.Agent.Tests;

public class AgentConfigReasoningTests
{
  private const string BaseYaml = """
    name: test
    model: test-model
    system_prompt: "s"
    user_prompt: "u"
    input_schema: {type: object}
    output_schema: {type: object}
    """;

  [Fact]
  public void Missing_reasoning_block_yields_null_reasoning()
  {
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(BaseYaml));
    cfg.Reasoning.Should().BeNull();
  }

  [Fact]
  public void Effort_only_parses_and_lowercases()
  {
    var yaml = BaseYaml + """

      reasoning:
        effort: Medium
      """;
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Reasoning.Should().NotBeNull();
    cfg.Reasoning!.Effort.Should().Be("medium");
    cfg.Reasoning.ThinkingBudgetTokens.Should().BeNull();
  }

  [Fact]
  public void Budget_only_parses()
  {
    var yaml = BaseYaml + """

      reasoning:
        thinking_budget_tokens: 2048
      """;
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Reasoning.Should().NotBeNull();
    cfg.Reasoning!.Effort.Should().BeNull();
    cfg.Reasoning.ThinkingBudgetTokens.Should().Be(2048);
  }

  [Fact]
  public void Both_set_parses()
  {
    var yaml = BaseYaml + """

      reasoning:
        effort: high
        thinking_budget_tokens: 4096
      """;
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Reasoning.Should().NotBeNull();
    cfg.Reasoning!.Effort.Should().Be("high");
    cfg.Reasoning.ThinkingBudgetTokens.Should().Be(4096);
  }

  [Fact]
  public void Unknown_effort_throws_ConfigException()
  {
    var yaml = BaseYaml + """

      reasoning:
        effort: spicy
      """;
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*effort*low*medium*high*");
  }

  [Fact]
  public void Budget_below_1024_throws_ConfigException()
  {
    var yaml = BaseYaml + """

      reasoning:
        thinking_budget_tokens: 100
      """;
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*1024*");
  }

  [Fact]
  public void Empty_reasoning_block_throws_ConfigException()
  {
    var yaml = BaseYaml + """

      reasoning: {}
      """;
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*effort*thinking_budget_tokens*");
  }
}

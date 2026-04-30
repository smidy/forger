using FluentAssertions;
using Forge.Core.Exceptions;
using Forge.Pipeline;

namespace Forge.Pipeline.Tests;

public class DagResolverTests
{
  [Fact]
  public void Topological_groups_independent_stages_in_same_group()
  {
    var cfg = new PipelineConfig
    {
      Name = "t",
      Stages = new List<StageConfig>
      {
        new() { Id = "a", DependsOn = new List<string>() },
        new() { Id = "b", DependsOn = new List<string>() },
        new() { Id = "c", DependsOn = new List<string> { "a", "b" } }
      }
    };

    var dag = DagResolver.Resolve(cfg);
    dag.Groups.Should().HaveCount(2);
    dag.Groups[0].Select(s => s.Id).Should().BeEquivalentTo(new[] { "a", "b" });
    dag.Groups[1].Select(s => s.Id).Should().BeEquivalentTo(new[] { "c" });
  }

  [Fact]
  public void Cycle_throws_PipelineException()
  {
    var cfg = new PipelineConfig
    {
      Name = "t",
      Stages = new List<StageConfig>
      {
        new() { Id = "a", DependsOn = new List<string> { "b" } },
        new() { Id = "b", DependsOn = new List<string> { "a" } }
      }
    };

    var act = () => DagResolver.Resolve(cfg);
    act.Should().Throw<PipelineException>();
  }

  [Fact]
  public void Unknown_dependency_throws_ConfigException()
  {
    var cfg = new PipelineConfig
    {
      Name = "t",
      Stages = new List<StageConfig>
      {
        new() { Id = "a", DependsOn = new List<string> { "missing" } }
      }
    };

    var act = () => DagResolver.Resolve(cfg);
    act.Should().Throw<ConfigException>();
  }
}

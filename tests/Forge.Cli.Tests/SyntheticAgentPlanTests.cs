using FluentAssertions;
using Forge.Cli.Commands;

namespace Forge.Cli.Tests;

public sealed class SyntheticAgentPlanTests
{
  [Fact]
  public void TryExtractAgentName_WithAgentPrefix_ReturnsName()
  {
    SyntheticAgentPlan.TryExtractAgentName("agent:forge-dev").Should().Be("forge-dev");
  }

  [Fact]
  public void TryExtractAgentName_WithHyphenatedName_ReturnsFullName()
  {
    SyntheticAgentPlan.TryExtractAgentName("agent:my-agent-name").Should().Be("my-agent-name");
  }

  [Fact]
  public void TryExtractAgentName_WithoutPrefix_ReturnsNull()
  {
    SyntheticAgentPlan.TryExtractAgentName("my-pipeline").Should().BeNull();
  }

  [Fact]
  public void TryExtractAgentName_EmptyString_ReturnsNull()
  {
    SyntheticAgentPlan.TryExtractAgentName("").Should().BeNull();
  }

  [Fact]
  public void TryExtractAgentName_CaseSensitive_NoMatch()
  {
    // Prefix match is ordinal case-sensitive — "Agent:" must NOT be treated as synthetic.
    SyntheticAgentPlan.TryExtractAgentName("Agent:forge-dev").Should().BeNull();
  }

  [Fact]
  public void BuildSyntheticPipeline_HappyPath_ShapeIsCorrect()
  {
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline("forge-dev");

    pipeline.Name.Should().Be("agent:forge-dev");
    pipeline.Version.Should().Be("1");
    pipeline.Stages.Should().HaveCount(1);
    pipeline.Stages[0].Id.Should().Be("agent");
    pipeline.Stages[0].Agent.Should().Be("forge-dev");
    pipeline.Stages[0].DependsOn.Should().BeEmpty();
  }

  [Fact]
  public void PipelineNamePrefix_IsStable()
  {
    // Changing this breaks every in-flight resumable agent run on disk.
    SyntheticAgentPlan.PipelineNamePrefix.Should().Be("agent:");
    SyntheticAgentPlan.StageId.Should().Be("agent");
  }

  [Fact]
  public void RoundTrip_BuildThenExtract_ReturnsOriginalName()
  {
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline("forge-dev");
    var extracted = SyntheticAgentPlan.TryExtractAgentName(pipeline.Name);
    extracted.Should().Be("forge-dev");
  }
}

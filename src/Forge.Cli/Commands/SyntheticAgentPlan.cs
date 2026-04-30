using Forge.Pipeline;

namespace Forge.Cli.Commands;

internal static class SyntheticAgentPlan
{
  public const string PipelineNamePrefix = "agent:";
  public const string StageId = "agent";

  public static string? TryExtractAgentName(string pipelineName) =>
    pipelineName.StartsWith(PipelineNamePrefix, StringComparison.Ordinal)
      ? pipelineName[PipelineNamePrefix.Length..]
      : null;

  public static PipelineConfig BuildSyntheticPipeline(string agentName) =>
    new()
    {
      Name = PipelineNamePrefix + agentName,
      Version = "1",
      Stages = new List<StageConfig>
      {
        new() { Id = StageId, Agent = agentName }
      }
    };
}

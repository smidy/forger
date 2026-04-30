using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Cli.Commands;
using Forge.Core.Workspace;
using Forge.Pipeline;

namespace Forge.Cli.Tests;

/// <summary>
/// Regression coverage for the synthetic-agent result projection that
/// <see cref="ResumeCommand"/> applies after <see cref="PipelineExecutor.ResumeAsync"/>
/// returns. The synthetic-agent pipeline declares no <c>outputs:</c>, so without
/// projection both the run-root <c>result.json</c> and stdout would render <c>{}</c>
/// even when <c>submit_final</c> wrote a real payload to <c>stages/agent/output.json</c>.
/// See bash-sandbox-seam.md eval Phase 7 finding F1.
/// </summary>
public sealed class ResumeCommandStageOutputProjectionTests : IDisposable
{
  private readonly string _runRoot;

  public ResumeCommandStageOutputProjectionTests()
  {
    _runRoot = Path.Combine(Path.GetTempPath(),
      $"forge-resume-projection-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_runRoot);
  }

  public void Dispose()
  {
    try
    {
      if (Directory.Exists(_runRoot))
      {
        Directory.Delete(_runRoot, recursive: true);
      }
    }
    catch
    {
    }
  }

  [Fact]
  public async Task NonAgentRun_ReturnsOriginalUnchanged()
  {
    var original = new PipelineResult { Result = new JsonObject { ["echo"] = "ok" } };

    var projected = await ResumeCommand.ProjectAgentStageOutputAsync(
      original, agentName: null, _runRoot, TestContext.Current.CancellationToken);

    projected.Should().BeSameAs(original);
    File.Exists(WorkspacePaths.ResultPath(_runRoot)).Should()
      .BeFalse("non-agent runs are left to the pipeline executor");
  }

  [Fact]
  public async Task AgentRun_NoStageOutputOnDisk_ReturnsOriginalUnchanged()
  {
    var original = new PipelineResult { Result = new JsonObject() };

    var projected = await ResumeCommand.ProjectAgentStageOutputAsync(
      original, agentName: "forge-dev", _runRoot, TestContext.Current.CancellationToken);

    projected.Should().BeSameAs(original);
    File.Exists(WorkspacePaths.ResultPath(_runRoot)).Should().BeFalse();
  }

  [Fact]
  public async Task AgentRun_StageOutputPresent_ProjectsOntoResultJsonAndReturn()
  {
    var stageDir = WorkspacePaths.StageDir(_runRoot, SyntheticAgentPlan.StageId);
    Directory.CreateDirectory(stageDir);
    var stageOutput = new JsonObject
    {
      ["summary"] = "landed",
      ["files_modified"] = new JsonArray("src/Forge.Tools/BashTool.cs"),
      ["open_questions"] = new JsonArray(),
      ["next_steps"] = new JsonArray(),
      ["implementation_notes_path"] = "/run/implementation_notes.md"
    };
    await File.WriteAllTextAsync(
      WorkspacePaths.StageOutputPath(stageDir),
      stageOutput.ToJsonString(),
      TestContext.Current.CancellationToken);

    var original = new PipelineResult { Result = new JsonObject() };

    var projected = await ResumeCommand.ProjectAgentStageOutputAsync(
      original, agentName: "forge-dev", _runRoot, TestContext.Current.CancellationToken);

    projected.Should().NotBeSameAs(original);
    projected.Result["summary"]!.GetValue<string>().Should().Be("landed");
    projected.Result["files_modified"]!.AsArray().Count.Should().Be(1);

    File.Exists(WorkspacePaths.ResultPath(_runRoot)).Should()
      .BeTrue("agent runs surface stage output as the run-root result.json");
    var written = await File.ReadAllTextAsync(
      WorkspacePaths.ResultPath(_runRoot), TestContext.Current.CancellationToken);
    var writtenNode = JsonNode.Parse(written)!;
    writtenNode["summary"]!.GetValue<string>().Should().Be("landed");
  }

  [Fact]
  public async Task AgentRun_MalformedStageOutput_ReturnsOriginalUnchanged()
  {
    var stageDir = WorkspacePaths.StageDir(_runRoot, SyntheticAgentPlan.StageId);
    Directory.CreateDirectory(stageDir);
    await File.WriteAllTextAsync(
      WorkspacePaths.StageOutputPath(stageDir),
      "not-json{{",
      TestContext.Current.CancellationToken);

    var original = new PipelineResult { Result = new JsonObject() };

    Func<Task> act = () => ResumeCommand.ProjectAgentStageOutputAsync(
      original, agentName: "forge-dev", _runRoot, TestContext.Current.CancellationToken);

    // JsonNode.Parse throws on invalid JSON; the helper surfaces it rather than
    // silently writing junk into result.json.
    await act.Should().ThrowAsync<System.Text.Json.JsonException>();
  }
}

using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Cli.Commands;
using Forge.Core.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Cli.Tests;

/// <summary>
/// ResumeCommand branching coverage: given a plan.json whose pipeline.name starts with
/// "agent:", ResumeCommand must take the agent lookup path; otherwise it must take the
/// pipeline lookup path. Both early-exit paths terminate before LLM / tool wiring is
/// required, so we cover them with a minimal service provider.
/// See docs/plans/forge-agent-resume-parity.md.
/// </summary>
public sealed class ResumeCommandAgentPathTests : IDisposable
{
  private readonly string _tempHome;

  public ResumeCommandAgentPathTests()
  {
    _tempHome = Path.Combine(Path.GetTempPath(), $"forge-resume-agentpath-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempHome);
  }

  public void Dispose()
  {
    try
    {
      if (Directory.Exists(_tempHome))
      {
        Directory.Delete(_tempHome, recursive: true);
      }
    }
    catch
    {
    }
  }

  private async Task WritePlanWithPipelineName(string runId, string pipelineName)
  {
    var runRoot = WorkspacePaths.RunRoot(_tempHome, runId);
    Directory.CreateDirectory(runRoot);
    var plan = new JsonObject
    {
      ["pipeline"] = new JsonObject { ["name"] = pipelineName, ["version"] = "1" },
      ["agents"] = new JsonArray(),
      ["tools"] = new JsonArray(),
      ["stages"] = new JsonArray(),
      ["resolvedInputs"] = new JsonObject()
    };
    await File.WriteAllTextAsync(WorkspacePaths.PlanPath(runRoot), plan.ToJsonString(), TestContext.Current.CancellationToken);
  }

  private IServiceProvider BuildMinimalSp()
  {
    var sc = new ServiceCollection();
    sc.AddSingleton(new ForgeAppState
    {
      ForgeHome = _tempHome,
      CancellationToken = TestContext.Current.CancellationToken
    });
    return sc.BuildServiceProvider();
  }

  [Fact]
  public async Task ExecuteAsync_EmptyRunId_ReturnsExit1()
  {
    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = "" };

    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }

  [Fact]
  public async Task ExecuteAsync_RunDirectoryMissing_ReturnsExit1()
  {
    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = "does-not-exist-20260423-aaaaaaaa" };

    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }

  [Fact]
  public async Task ExecuteAsync_PlanJsonMissing_ReturnsExit1()
  {
    var runId = "test-run-20260423-aaaaaaaa";
    Directory.CreateDirectory(WorkspacePaths.RunRoot(_tempHome, runId));
    // plan.json deliberately not written

    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = runId };

    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }

  [Fact]
  public async Task ExecuteAsync_AgentPrefix_AgentNotFound_ReturnsExit1()
  {
    var runId = "test-run-20260423-aaaaaaaa";
    // Guid-scoped name prevents accidental hits against `<cwd>/.forge/agents/` which
    // PluginPaths.SearchRoots checks before our temp home.
    var uniqueName = "agent-" + Guid.NewGuid().ToString("N");
    await WritePlanWithPipelineName(runId, "agent:" + uniqueName);

    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = runId };

    // No agent YAML exists under tempHome/agents/; branching into the agent path
    // with a missing agent yields exit 1 without touching Resumer / LLM / tools.
    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }

  [Fact]
  public async Task ExecuteAsync_PipelineName_PipelineNotFound_ReturnsExit1()
  {
    var runId = "test-run-20260423-aaaaaaaa";
    var uniqueName = "pipeline-" + Guid.NewGuid().ToString("N");
    await WritePlanWithPipelineName(runId, uniqueName);

    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = runId };

    // Plan name without "agent:" prefix routes to FindPipeline; missing pipeline
    // yields exit 1 without touching Resumer.
    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }

  [Fact]
  public async Task ExecuteAsync_EmptyPipelineName_ReturnsExit1()
  {
    var runId = "test-run-20260423-aaaaaaaa";
    await WritePlanWithPipelineName(runId, "");

    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = runId };

    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }

  [Fact]
  public async Task ExecuteAsync_MalformedPlanJson_ReturnsExit1()
  {
    var runId = "test-run-20260423-aaaaaaaa";
    var runRoot = WorkspacePaths.RunRoot(_tempHome, runId);
    Directory.CreateDirectory(runRoot);
    await File.WriteAllTextAsync(WorkspacePaths.PlanPath(runRoot), "not-valid-json{{", TestContext.Current.CancellationToken);

    var sp = BuildMinimalSp();
    var settings = new ResumeSettings { RunId = runId };

    var exit = await ResumeCommand.ExecuteAsync(sp, settings);
    exit.Should().Be(1);
  }
}

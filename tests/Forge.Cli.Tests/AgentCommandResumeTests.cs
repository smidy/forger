using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Cli.Commands;
using Forge.Core.Trace;
using Forge.Core.Workspace;
using Forge.Pipeline;
using Forge.Tools;

namespace Forge.Cli.Tests;

/// <summary>
/// Plan-write coverage for `forge agent`: a bare agent run must emit a synthetic one-stage
/// `plan.json` whose `agents[0].schemaHash` matches exactly what `Resumer.HydrateAsync` will
/// recompute at resume time. See docs/plans/forge-agent-resume-parity.md.
/// </summary>
public sealed class AgentCommandResumeTests : IDisposable
{
  private readonly string _tempHome;
  private readonly string _agentName;

  public AgentCommandResumeTests()
  {
    _tempHome = Path.Combine(Path.GetTempPath(), $"forge-agent-resume-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempHome);
    Directory.CreateDirectory(Path.Combine(_tempHome, "agents"));
    // Unique agent name prevents collision with any real `<cwd>/.forge/agents/` entry
    // on dev machines (PluginPaths.SearchRoots checks cwd first).
    _agentName = $"syn-test-{Guid.NewGuid():N}";
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

  private string WriteMinimalAgentYaml(string? name = null)
  {
    var agentName = name ?? _agentName;
    var yaml = $@"name: {agentName}
model: test-model
system_prompt: ""You are a test agent.""
user_prompt: ""Do the thing.""
max_iterations: 3
tools: []
input_schema:
  type: object
output_schema:
  type: object
  properties:
    result:
      type: string
  required: [result]
";
    var path = Path.Combine(_tempHome, "agents", agentName + ".agent.yaml");
    File.WriteAllText(path, yaml);
    return path;
  }

  [Fact]
  public async Task WritePlanJsonAsync_WithSyntheticPipeline_EmitsExpectedPlanShape()
  {
    WriteMinimalAgentYaml();
    var runRoot = Path.Combine(_tempHome, "runs", "test-run");
    Directory.CreateDirectory(runRoot);
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline(_agentName);

    await PipelineExecutor.WritePlanJsonAsync(
      pipeline,
      new JsonObject(),
      runRoot,
      _tempHome,
      new ToolRegistry(),
      new NullTraceSink(),
      TestContext.Current.CancellationToken);

    var planPath = WorkspacePaths.PlanPath(runRoot);
    File.Exists(planPath).Should().BeTrue();

    var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath, TestContext.Current.CancellationToken))!.AsObject();
    plan["pipeline"]!["name"]!.GetValue<string>().Should().Be("agent:" + _agentName);
    plan["pipeline"]!["version"]!.GetValue<string>().Should().Be("1");

    var stages = plan["stages"]!.AsArray();
    stages.Should().HaveCount(1);
    stages[0]!["id"]!.GetValue<string>().Should().Be("agent");
    stages[0]!["dependencies"]!.AsArray().Should().BeEmpty();

    var agents = plan["agents"]!.AsArray();
    agents.Should().HaveCount(1);
    agents[0]!["name"]!.GetValue<string>().Should().Be(_agentName);
    agents[0]!["schemaHash"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    agents[0]!["promptHash"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
  }

  [Fact]
  public async Task SchemaHash_MatchesResumerRecomputation()
  {
    // This is the core hash-parity test. If this fails, every `forge resume` on an agent
    // run would throw "Schema hash mismatch" — blocking the very feature we're building.
    var agentPath = WriteMinimalAgentYaml();
    var runRoot = Path.Combine(_tempHome, "runs", "test-run");
    Directory.CreateDirectory(runRoot);
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline(_agentName);

    await PipelineExecutor.WritePlanJsonAsync(
      pipeline,
      new JsonObject(),
      runRoot,
      _tempHome,
      new ToolRegistry(),
      new NullTraceSink(),
      TestContext.Current.CancellationToken);

    var plan = JsonNode.Parse(await File.ReadAllTextAsync(WorkspacePaths.PlanPath(runRoot), TestContext.Current.CancellationToken))!.AsObject();
    var planSchemaHash = plan["agents"]!.AsArray()[0]!["schemaHash"]!.GetValue<string>();

    // Recompute exactly the way Resumer.HydrateAsync does — {name, inputSchema, outputSchema}.
    var cfg = AgentConfig.LoadFromYamlFile(agentPath);
    var expectedPayload = new JsonObject
    {
      ["name"] = cfg.Name,
      ["inputSchema"] = cfg.InputSchema.DeepClone(),
      ["outputSchema"] = cfg.OutputSchema?.DeepClone()
    };
    var expectedHash = PipelineExecutor.ComputeHash(expectedPayload);

    planSchemaHash.Should().Be(expectedHash,
      "plan.json agent schemaHash must match Resumer's recomputation or every resume fails");
  }

  [Fact]
  public async Task PromptHash_MatchesResumerRecomputation()
  {
    var agentPath = WriteMinimalAgentYaml();
    var runRoot = Path.Combine(_tempHome, "runs", "test-run");
    Directory.CreateDirectory(runRoot);
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline(_agentName);

    await PipelineExecutor.WritePlanJsonAsync(
      pipeline,
      new JsonObject(),
      runRoot,
      _tempHome,
      new ToolRegistry(),
      new NullTraceSink(),
      TestContext.Current.CancellationToken);

    var plan = JsonNode.Parse(await File.ReadAllTextAsync(WorkspacePaths.PlanPath(runRoot), TestContext.Current.CancellationToken))!.AsObject();
    var planPromptHash = plan["agents"]!.AsArray()[0]!["promptHash"]!.GetValue<string>();

    var cfg = AgentConfig.LoadFromYamlFile(agentPath);
    var expectedPayload = new JsonObject
    {
      ["systemPrompt"] = cfg.SystemPrompt,
      ["userPrompt"] = cfg.UserPrompt,
      ["model"] = cfg.Model,
      ["toolNames"] = new JsonArray(cfg.Tools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
      ["maxIterations"] = cfg.MaxIterations
    };
    var expectedHash = PipelineExecutor.ComputeHash(expectedPayload);

    planPromptHash.Should().Be(expectedHash);
  }

  [Fact]
  public async Task ResolvedInputs_ArePreservedInPlan()
  {
    WriteMinimalAgentYaml();
    var runRoot = Path.Combine(_tempHome, "runs", "test-run");
    Directory.CreateDirectory(runRoot);
    var input = new JsonObject
    {
      ["goal"] = "find-bugs",
      ["topK"] = 3
    };
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline(_agentName);

    await PipelineExecutor.WritePlanJsonAsync(
      pipeline,
      input,
      runRoot,
      _tempHome,
      new ToolRegistry(),
      new NullTraceSink(),
      TestContext.Current.CancellationToken);

    var plan = JsonNode.Parse(await File.ReadAllTextAsync(WorkspacePaths.PlanPath(runRoot), TestContext.Current.CancellationToken))!.AsObject();
    plan["resolvedInputs"]!["goal"]!.GetValue<string>().Should().Be("find-bugs");
    plan["resolvedInputs"]!["topK"]!.GetValue<int>().Should().Be(3);
  }

  [Fact]
  public async Task PipelinePrefix_RoundTripsThroughTryExtractAgentName()
  {
    // End-to-end guarantee: written plan.json's pipeline.name is recognized by the
    // same helper ResumeCommand uses to branch.
    WriteMinimalAgentYaml();
    var runRoot = Path.Combine(_tempHome, "runs", "test-run");
    Directory.CreateDirectory(runRoot);
    var pipeline = SyntheticAgentPlan.BuildSyntheticPipeline(_agentName);

    await PipelineExecutor.WritePlanJsonAsync(
      pipeline,
      new JsonObject(),
      runRoot,
      _tempHome,
      new ToolRegistry(),
      new NullTraceSink(),
      TestContext.Current.CancellationToken);

    var plan = JsonNode.Parse(await File.ReadAllTextAsync(WorkspacePaths.PlanPath(runRoot), TestContext.Current.CancellationToken))!.AsObject();
    var pipelineName = plan["pipeline"]!["name"]!.GetValue<string>();
    var extracted = SyntheticAgentPlan.TryExtractAgentName(pipelineName);

    extracted.Should().Be(_agentName);
  }

  private sealed class NullTraceSink : ITraceSink
  {
    public void Trace(TraceEvent e) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}

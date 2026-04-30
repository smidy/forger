using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Exceptions;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Core.Trace;
using Forge.Pipeline;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Pipeline.Tests;

public class PipelineExecutorTests
{
  [Fact]
  public void ComputeHash_same_result_for_different_key_insertion_order()
  {
    var a = new JsonObject { ["x"] = 1, ["y"] = 2 };
    var b = new JsonObject { ["y"] = 2, ["x"] = 1 };
    PipelineExecutor.ComputeHash(a).Should().Be(PipelineExecutor.ComputeHash(b));
  }

  [Fact]
  public void ComputeHash_produces_lowercase_hex_string()
  {
    var node = new JsonObject { ["k"] = "v" };
    var hash = PipelineExecutor.ComputeHash(node);
    hash.Should().MatchRegex("^[0-9a-f]{64}$");
  }

  [Fact]
  public async Task WritePlanJson_writes_expected_top_level_fields()
  {
    var ct = TestContext.Current.CancellationToken;
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try
    {
      var pipeline = new PipelineConfig
      {
        Name = "test-pipeline",
        Version = "42",
        Stages = new List<StageConfig>()
      };
      await using var trace = new TraceSink(WorkspacePaths.TracePath(tmpDir));
      await PipelineExecutor.WritePlanJsonAsync(pipeline, new JsonObject(), tmpDir, Path.GetTempPath(), new ToolRegistry(), trace, ct);

      var planPath = WorkspacePaths.PlanPath(tmpDir);
      File.Exists(planPath).Should().BeTrue();
      var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath, ct))!.AsObject();
      plan["pipeline"]!["name"]!.GetValue<string>().Should().Be("test-pipeline");
      plan["pipeline"]!["version"]!.GetValue<string>().Should().Be("42");
      plan["pipeline"]!.AsObject().ContainsKey("schemaHash").Should().BeTrue();
      plan["pipeline"]!.AsObject().ContainsKey("promptHash").Should().BeTrue();
      plan["agents"]!.AsArray().Should().BeEmpty();
      plan["tools"]!.AsArray().Should().BeEmpty();
      plan["stages"]!.AsArray().Should().BeEmpty();
      plan.ContainsKey("resolvedInputs").Should().BeTrue();
    }
    finally
    {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Fact]
  public async Task FanOut_OnErrorContinue_collects_errors_and_continues()
  {
    var ct = TestContext.Current.CancellationToken;
    var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
    try
    {
      var tools = new ToolRegistry();
      tools.Register(new FailOnValueTool());

      // fan_out over [1, 2, 3] — tool fails when value = 2 (index 1)
      var pipeline = new PipelineConfig
      {
        Name = "fanout-test",
        Stages = new List<StageConfig>
        {
          new()
          {
            Id = "scatter",
            Tool = "fail-on-value",
            FanOut = "$.input.items[*]",
            OnError = "continue",
            Input = new JsonObject { ["value"] = "$item" }
          }
        }
      };

      var pipelineInput = new JsonObject
      {
        ["items"] = new JsonArray(
          JsonValue.Create(1),
          JsonValue.Create(2),
          JsonValue.Create(3))
      };

      Func<Task> act = () => PipelineExecutor.RunAsync(
        pipeline,
        pipelineInput,
        tmpForgeHome,
        null!,
        tools,
        NullLoggerFactory.Instance,
        ct);

      await act.Should().ThrowAsync<PartialFailureException>();

      var runsDir = Path.Combine(tmpForgeHome, "runs");
      var runDirs = Directory.GetDirectories(runsDir);
      runDirs.Should().HaveCount(1);

      var runRoot = runDirs[0];
      var stageOutputPath = WorkspacePaths.StageOutputPath(
        WorkspacePaths.StageDir(runRoot, "scatter"));
      File.Exists(stageOutputPath).Should().BeTrue();

      var stageOutput = JsonNode.Parse(await File.ReadAllTextAsync(stageOutputPath, ct))!.AsObject();
      stageOutput["ok"]!.AsArray().Should().HaveCount(2);
      stageOutput["errors"]!.AsArray().Should().HaveCount(1);
      stageOutput["errors"]![0]!.AsObject()["index"]!.GetValue<int>().Should().Be(1);
    }
    finally
    {
      Directory.Delete(tmpForgeHome, recursive: true);
    }
  }

  [Fact]
  public async Task FanOut_OnErrorFail_propagates_iteration_exception()
  {
    var ct = TestContext.Current.CancellationToken;
    var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
    try
    {
      var tools = new ToolRegistry();
      tools.Register(new FailOnValueTool());

      var pipeline = new PipelineConfig
      {
        Name = "fanout-fail-test",
        Stages = new List<StageConfig>
        {
          new()
          {
            Id = "scatter",
            Tool = "fail-on-value",
            FanOut = "$.input.items[*]",
            // on_error defaults to "fail"
            Input = new JsonObject { ["value"] = "$item" }
          }
        }
      };

      var pipelineInput = new JsonObject
      {
        ["items"] = new JsonArray(JsonValue.Create(2))
      };

      Func<Task> act = () => PipelineExecutor.RunAsync(
        pipeline,
        pipelineInput,
        tmpForgeHome,
        null!,
        tools,
        NullLoggerFactory.Instance,
        ct);

      // Default on_error: fail — exception propagates (not PartialFailureException)
      await act.Should().ThrowAsync<Exception>()
        .Where(ex => ex.GetType() != typeof(PartialFailureException));
    }
    finally
    {
      Directory.Delete(tmpForgeHome, recursive: true);
    }
  }

  // --- helpers ---

  private sealed record FailInput
  {
    public int Value { get; init; }
  }

  private sealed record FailOutput
  {
    public int Value { get; init; }
  }

  private sealed class FailOnValueTool : ToolBase<FailInput, FailOutput>
  {
    public override string Name => "fail-on-value";
    public override string Description => "Fails when Value == 2.";

    protected override Task<FailOutput> ExecuteCoreAsync(FailInput input, ToolContext ctx, CancellationToken cancellationToken)
    {
      if (input.Value == 2)
      {
        throw new InvalidOperationException("Value is 2, failing intentionally.");
      }

      return Task.FromResult(new FailOutput { Value = input.Value });
    }
  }
}

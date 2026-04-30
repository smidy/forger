using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;
using Forge.Core.Filesystem;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Pipeline;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.EndToEnd.Tests;

public class ResumeAfter429Tests
{
  [Fact]
  public async Task RateLimit_on_iter1_leaves_iter0_state_json_intact_and_resume_succeeds()
  {
    var ct = TestContext.Current.CancellationToken;
    var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
    var agentsDir = Path.Combine(tmpForgeHome, "agents");
    Directory.CreateDirectory(agentsDir);

    try
    {
      // Agent that does noop on iter 0, then gets 429'd on iter 1
      var agentYaml = """
name: test-agent
model: test-model
system_prompt: You are a test agent.
user_prompt: Do something.
max_iterations: 10
tools: []
input_schema: { "type": "object" }
output_schema:
  type: object
  properties:
    done:
      type: boolean
  required: [done]
""";
      var agentPath = Path.Combine(agentsDir, "test-agent.agent.yaml");
      await File.WriteAllTextAsync(agentPath, agentYaml, ct);

      var pipeline = new PipelineConfig
      {
        Name = "resume-429-test",
        Stages = new List<StageConfig>
        {
          new()
          {
            Id = "stage1",
            Agent = "test-agent"
          }
        }
      };

      var tools = new ToolRegistry();
      tools.Register(new NoopTool());

      // First run: returns noop on iter 0, RateLimitedException on iter 1
      var llm = new RateLimitAfterIterLlmClient(failAfterIter: 0);
      var pipelineInput = new JsonObject();

      Func<Task> firstRun = () => PipelineExecutor.RunAsync(
        pipeline,
        pipelineInput,
        tmpForgeHome,
        llm,
        tools,
        NullLoggerFactory.Instance,
        ct);

      var ex = await firstRun.Should().ThrowAsync<RateLimitedException>();
      ex.Which.RetryAfter.Should().NotBeNull();

      // Find the run directory
      var runsDir = Path.Combine(tmpForgeHome, "runs");
      var runDirs = Directory.GetDirectories(runsDir);
      runDirs.Should().HaveCount(1);
      var runRoot = runDirs[0];
      var runId = Path.GetFileName(runRoot);

      // Verify iter 0 state.json exists
      var stageDir = WorkspacePaths.StageDir(runRoot, "stage1");
      var iter0State = Path.Combine(stageDir, "iterations", "000", "state.json");
      File.Exists(iter0State).Should().BeTrue();

      // Resume with a healthy LLM
      var healthyLlm = new SimpleSubmitFinalLlm();
      var resumeState = await Resumer.HydrateAsync(
        runRoot, pipeline, tools, tmpForgeHome, force: false, ct);

      var resumeResult = await PipelineExecutor.ResumeAsync(
        runId,
        runRoot,
        pipeline,
        resumeState,
        tmpForgeHome,
        healthyLlm,
        tools,
        NullLoggerFactory.Instance,
        ct);

      // Verify stage1 completed by checking output
      var stageOutputPath = WorkspacePaths.StageOutputPath(stageDir);
      File.Exists(stageOutputPath).Should().BeTrue();

      // Verify trace contains StageResumedFromIterEvent (resume from iter 1)
      var tracePath = WorkspacePaths.TracePath(runRoot);
      var traceLines = await File.ReadAllLinesAsync(tracePath, ct);
      var resumedEvent = traceLines
        .Select(l => JsonNode.Parse(l))
        .FirstOrDefault(n => n?["kind"]?.GetValue<string>() == "stage_resumed_from_iter");
      resumedEvent.Should().NotBeNull();
      resumedEvent!["stageId"]!.GetValue<string>().Should().Be("stage1");
      resumedEvent["fromIter"]!.GetValue<int>().Should().Be(1);
    }
    finally
    {
      try { Directory.Delete(tmpForgeHome, recursive: true); } catch { /* best-effort */ }
    }
  }

  [Fact]
  public async Task RateLimit_on_first_call_leaves_no_state_json_resume_reruns_from_iter0()
  {
    var ct = TestContext.Current.CancellationToken;
    var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
    var agentsDir = Path.Combine(tmpForgeHome, "agents");
    Directory.CreateDirectory(agentsDir);

    try
    {
      var agentYaml = """
name: test-agent
model: test-model
system_prompt: You are a test agent.
user_prompt: Do something.
max_iterations: 10
tools: []
input_schema: { "type": "object" }
output_schema:
  type: object
  properties:
    done:
      type: boolean
  required: [done]
""";
      var agentPath = Path.Combine(agentsDir, "test-agent.agent.yaml");
      await File.WriteAllTextAsync(agentPath, agentYaml, ct);

      var pipeline = new PipelineConfig
      {
        Name = "resume-first-429-test",
        Stages = new List<StageConfig>
        {
          new()
          {
            Id = "stage1",
            Agent = "test-agent"
          }
        }
      };

      var tools = new ToolRegistry();

      // LLM that always throws RateLimitedException (never produces any response)
      var llm = new AlwaysRateLimitedLlm();
      var pipelineInput = new JsonObject();

      Func<Task> firstRun = () => PipelineExecutor.RunAsync(
        pipeline,
        pipelineInput,
        tmpForgeHome,
        llm,
        tools,
        NullLoggerFactory.Instance,
        ct);

      await firstRun.Should().ThrowAsync<RateLimitedException>();

      // Find the run directory
      var runsDir = Path.Combine(tmpForgeHome, "runs");
      var runDirs = Directory.GetDirectories(runsDir);
      runDirs.Should().HaveCount(1);
      var runRoot = runDirs[0];
      var runId = Path.GetFileName(runRoot);

      // No state.json should exist (first LLM call failed)
      var stageDir = WorkspacePaths.StageDir(runRoot, "stage1");
      var iterationsDir = Path.Combine(stageDir, "iterations");
      // iterations dir may not exist, or may be empty
      if (Directory.Exists(iterationsDir))
      {
        Directory.GetDirectories(iterationsDir).Should().BeEmpty();
      }

      // Resume with a healthy LLM — should rerun from iter 0
      var healthyLlm = new SimpleSubmitFinalLlm();
      var resumeState = await Resumer.HydrateAsync(
        runRoot, pipeline, tools, tmpForgeHome, force: false, ct);

      var resumeResult = await PipelineExecutor.ResumeAsync(
        runId,
        runRoot,
        pipeline,
        resumeState,
        tmpForgeHome,
        healthyLlm,
        tools,
        NullLoggerFactory.Instance,
        ct);

      // Verify stage1 completed
      var stageOutputPath = WorkspacePaths.StageOutputPath(stageDir);
      File.Exists(stageOutputPath).Should().BeTrue();
    }
    finally
    {
      try { Directory.Delete(tmpForgeHome, recursive: true); } catch { /* best-effort */ }
    }
  }

  // ─── Helpers ──────────────────────────────────────────────────────────────

  private sealed class NoopTool : ToolBase<JsonObject, JsonObject>
  {
    public override string Name => "noop";
    public override string Description => "A tool that does nothing.";

    protected override Task<JsonObject> ExecuteCoreAsync(JsonObject input, ToolContext ctx, CancellationToken cancellationToken)
      => Task.FromResult(new JsonObject());
  }

  /// <summary>
  /// LLM that returns a noop tool call for the first <c>failAfterIter</c> iterations
  /// (so snapshots are produced), then throws <see cref="RateLimitedException"/>.
  /// </summary>
  private sealed class RateLimitAfterIterLlmClient : ILlmClient
  {
    private readonly int _failAfterIter;
    private int _callCount;

    public RateLimitAfterIterLlmClient(int failAfterIter) => _failAfterIter = failAfterIter;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      var iter = _callCount++;
      if (iter > _failAfterIter)
      {
        throw new RateLimitedException($"Rate limited at call {iter}", TimeSpan.FromSeconds(1));
      }

      return Task.FromResult(new CompletionResponse
      {
        Id = $"iter-{iter}",
        Choices = new()
        {
          new CompletionChoice
          {
            Index = 0,
            FinishReason = "tool_calls",
            Message = new ChatMessagePayload
            {
              Role = "assistant",
              ToolCalls = new()
              {
                new ToolCallPayload
                {
                  Id = $"call-{iter}",
                  Type = "function",
                  Function = new FunctionCallPayload { Name = "noop", Arguments = "{}" }
                }
              }
            }
          }
        },
        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
      });
    }
  }

  /// <summary>LLM that always throws RateLimitedException.</summary>
  private sealed class AlwaysRateLimitedLlm : ILlmClient
  {
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
      => throw new RateLimitedException("Always rate limited", TimeSpan.FromSeconds(5));
  }

  /// <summary>LLM that always returns submit_final.</summary>
  private sealed class SimpleSubmitFinalLlm : ILlmClient
  {
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
      => Task.FromResult(new CompletionResponse
      {
        Id = "final",
        Choices = new()
        {
          new CompletionChoice
          {
            Index = 0,
            FinishReason = "tool_calls",
            Message = new ChatMessagePayload
            {
              Role = "assistant",
              ToolCalls = new()
              {
                new ToolCallPayload
                {
                  Id = "final-call",
                  Type = "function",
                  Function = new FunctionCallPayload
                  {
                    Name = "submit_final",
                    Arguments = "{\"done\": true}"
                  }
                }
              }
            }
          }
        },
        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
      });
  }
}

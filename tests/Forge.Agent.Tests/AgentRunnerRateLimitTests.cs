using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Agent.Tests;

public class AgentRunnerRateLimitTests
{
  /// <summary>
  /// When the LLM client has already handled a transient 429 internally (via its
  /// own retry), the agent receives a normal 200 response and completes normally.
  /// </summary>
  [Fact]
  public async Task Agent_completes_normally_when_client_handled_429_transparently()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // No exceptions — the client handled the retry internally
    var llm = new SimpleSubmitFinalLlm();
    var trace = new CapturingTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig();

    var result = await AgentRunner.RunAsync(
      cfg,
      new JsonObject(),
      ctx,
      new ToolRegistry(),
      agentYamlPath: null,
      TestContext.Current.CancellationToken);

    result["done"]!.GetValue<bool>().Should().BeTrue();
    trace.Events.OfType<AgentIterationEvent>().Should().NotBeEmpty();
  }

  /// <summary>
  /// When the LLM client exhausts retries and throws RateLimitedException, the
  /// agent run fails, but the prior iteration's state snapshot survives intact.
  /// </summary>
  [Fact]
  public async Task Exhausted_retries_bubbles_RateLimitedException_and_leaves_state_json_intact()
  {
    using var tmp = new TempDir();
    var runRoot = Path.Combine(tmp.Path, "run");
    Directory.CreateDirectory(runRoot);
    var stageDir = WorkspacePaths.StageDir(runRoot, "agent");
    Directory.CreateDirectory(stageDir);

    // First LLM call returns a noop tool call (so we get a state snapshot at iter 0).
    // Second LLM call throws RateLimitedException.
    var calls = 0;
    var llm = new RateLimitedLlmClient(iteration =>
    {
      calls++;
      if (calls == 1)
      {
        return NoopToolCall();
      }
      throw new RateLimitedException("Rate limited — exhausted retries", null);
    });

    var tools = new ToolRegistry();
    tools.Register(new NoopTool());

    var trace = new CapturingTraceSink();
    var ctx = BuildContext(runRoot, stageDir, llm, trace);
    var cfg = BuildAgentConfig();

    Func<Task> act = () => AgentRunner.RunAsync(
      cfg,
      new JsonObject(),
      ctx,
      tools,
      agentYamlPath: null,
      TestContext.Current.CancellationToken);

    var ex = await act.Should().ThrowAsync<RateLimitedException>();
    ex.Which.RetryAfter.Should().BeNull();

    // iter 0 state snapshot should exist
    var iter0State = Path.Combine(stageDir, "iterations", "000", "state.json");
    File.Exists(iter0State).Should().BeTrue();
  }

  /// <summary>
  /// When the LLM client detects quota exhaustion, it throws QuotaExhaustedException
  /// and the agent run fails.
  /// </summary>
  [Fact]
  public async Task QuotaExhausted_bubbles_QuotaExhaustedException()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var llm = new RateLimitedLlmClient(_ =>
      throw new QuotaExhaustedException("Quota exhausted"));

    var trace = new CapturingTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig();

    Func<Task> act = () => AgentRunner.RunAsync(
      cfg,
      new JsonObject(),
      ctx,
      new ToolRegistry(),
      agentYamlPath: null,
      TestContext.Current.CancellationToken);

    await act.Should().ThrowAsync<QuotaExhaustedException>();
  }

  // ─── Helpers ──────────────────────────────────────────────────────────────

  private static AgentConfig BuildAgentConfig(int maxIterations = 4) => new()
  {
    Name = "test",
    Model = "test-model",
    SystemPrompt = "s",
    UserPrompt = "u",
    MaxIterations = maxIterations,
    Tools = new(),
    InputSchema = new JsonObject { ["type"] = "object" },
    OutputSchema = new JsonObject
    {
      ["type"] = "object",
      ["properties"] = new JsonObject { ["done"] = new JsonObject { ["type"] = "boolean" } },
      ["required"] = new JsonArray("done")
    },
    InjectProjectContext = false,
    InjectSkillsCatalog = false,
    DiffVerification = new AgentDiffVerificationConfig { Enabled = false }
  };

  private static ToolContext BuildContext(string runRoot, string stageDir, ILlmClient llm, ITraceSink trace)
  {
    var idx = 0;
    return new ToolContext(
      RunId: "test-run",
      RunWorkspace: runRoot,
      StageDir: stageDir,
      StageId: "agent",
      IterationIndex: null,
      Llm: llm,
      Trace: trace,
      Logger: NullLogger.Instance,
      CancellationToken: CancellationToken.None,
      NextToolOutputIdx: () => ++idx);
  }

  private static CompletionResponse NoopToolCall() => new()
  {
    Id = "r",
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
              Id = "call-1",
              Type = "function",
              Function = new FunctionCallPayload { Name = "noop", Arguments = "{}" }
            }
          }
        }
      }
    },
    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
  };

  private sealed class RateLimitedLlmClient : ILlmClient
  {
    private readonly Func<int, CompletionResponse> _responseFunc;
    private int _callCount;

    public RateLimitedLlmClient(Func<int, CompletionResponse> responseFunc) => _responseFunc = responseFunc;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      var result = _responseFunc(_callCount++);
      return Task.FromResult(result);
    }
  }

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
                  Function = new FunctionCallPayload { Name = "submit_final", Arguments = "{\"done\": true}" }
                }
              }
            }
          }
        },
        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
      });
  }

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class NoopTool : ToolBase<JsonObject, JsonObject>
  {
    public override string Name => "noop";
    public override string Description => "A tool that does nothing.";

    protected override Task<JsonObject> ExecuteCoreAsync(JsonObject input, ToolContext ctx, CancellationToken cancellationToken)
      => Task.FromResult(new JsonObject());
  }

  private sealed class TempDir : IDisposable
  {
    public string Path { get; }

    public TempDir()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
      try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }
  }
}

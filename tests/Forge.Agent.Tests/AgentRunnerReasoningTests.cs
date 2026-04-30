using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Agent.Tests;

public class AgentRunnerReasoningTests
{
  [Fact]
  public async Task Reasoning_content_is_persisted_and_event_emitted()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var llm = new FakeLlmClient(CompletionWithReasoning("here is my reasoning text"));
    var trace = new CapturingTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig();

    var result = await AgentRunner.RunAsync(cfg, new JsonObject(), ctx, new ToolRegistry(), agentYamlPath: null, TestContext.Current.CancellationToken);

    result["done"]!.GetValue<bool>().Should().BeTrue();

    var artifact = Path.Combine(WorkspacePaths.IterationDir(stageDir, 0), "reasoning.txt");
    File.Exists(artifact).Should().BeTrue();
    (await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken))
      .Should().Contain("here is my reasoning text");

    var persisted = trace.Events.OfType<ReasoningPersistedEvent>().Should().ContainSingle().Subject;
    persisted.Iteration.Should().Be(0);
    persisted.ArtifactPath.Should().Be(Path.GetFullPath(artifact));
    persisted.Bytes.Should().BeGreaterThan(0);
    persisted.HasThinkingBlocks.Should().BeFalse();

    var llmEvent = trace.Events.OfType<LlmCallEvent>().First();
    llmEvent.ReasoningContentPresent.Should().BeTrue();
    llmEvent.ThinkingBlocksPresent.Should().BeFalse();
  }

  [Fact]
  public async Task Thinking_blocks_are_persisted_with_signature_headers()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var message = CompletionWithThinkingBlocks(
      new[] { ("sig-1", "block-one-text"), ("sig-2", "block-two-text") });

    var llm = new FakeLlmClient(message);
    var trace = new CapturingTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig();

    await AgentRunner.RunAsync(cfg, new JsonObject(), ctx, new ToolRegistry(), agentYamlPath: null, TestContext.Current.CancellationToken);

    var artifact = Path.Combine(WorkspacePaths.IterationDir(stageDir, 0), "reasoning.txt");
    var text = await File.ReadAllTextAsync(artifact, TestContext.Current.CancellationToken);

    text.Should().Contain("--- block 1 (signature: sig-1) ---");
    text.Should().Contain("block-one-text");
    text.Should().Contain("--- block 2 (signature: sig-2) ---");
    text.Should().Contain("block-two-text");

    var persisted = trace.Events.OfType<ReasoningPersistedEvent>().Should().ContainSingle().Subject;
    persisted.HasThinkingBlocks.Should().BeTrue();
  }

  [Fact]
  public async Task No_reasoning_payload_writes_nothing_and_emits_no_event()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var llm = new FakeLlmClient(SubmitFinalOnly());
    var trace = new CapturingTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig();

    await AgentRunner.RunAsync(cfg, new JsonObject(), ctx, new ToolRegistry(), agentYamlPath: null, TestContext.Current.CancellationToken);

    var iterationDir = WorkspacePaths.IterationDir(stageDir, 0);
    File.Exists(Path.Combine(iterationDir, "reasoning.txt")).Should()
      .BeFalse("no reasoning content means reasoning.txt is not written");
    File.Exists(Path.Combine(iterationDir, "state.json")).Should()
      .BeTrue("the submit_final iteration always persists state.json regardless of reasoning content");

    trace.Events.OfType<ReasoningPersistedEvent>().Should().BeEmpty();

    var llmEvent = trace.Events.OfType<LlmCallEvent>().First();
    llmEvent.ReasoningContentPresent.Should().BeFalse();
    llmEvent.ThinkingBlocksPresent.Should().BeFalse();
  }

  [Fact]
  public async Task Config_reasoning_forwards_effort_onto_request()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var llm = new FakeLlmClient(SubmitFinalOnly());
    var trace = new CapturingTraceSink();
    var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
    var cfg = BuildAgentConfig(reasoning: new AgentReasoningConfig { Effort = "high", ThinkingBudgetTokens = 2048 });

    await AgentRunner.RunAsync(cfg, new JsonObject(), ctx, new ToolRegistry(), agentYamlPath: null, TestContext.Current.CancellationToken);

    var seen = llm.Requests.Should().ContainSingle().Subject;
    seen.ReasoningEffort.Should().Be("high");
    seen.ThinkingBudgetTokens.Should().Be(2048);
  }

  private static AgentConfig BuildAgentConfig(AgentReasoningConfig? reasoning = null) => new()
  {
    Name = "test",
    Model = "test-model",
    SystemPrompt = "s",
    UserPrompt = "u",
    MaxIterations = 4,
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
    Reasoning = reasoning
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

  private static CompletionResponse CompletionWithReasoning(string reasoningText) => new()
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
          ReasoningContent = JsonValue.Create(reasoningText),
          ToolCalls = new()
          {
            new ToolCallPayload
            {
              Id = "call-1",
              Type = "function",
              Function = new FunctionCallPayload { Name = "submit_final", Arguments = """{"done": true}""" }
            }
          }
        }
      }
    },
    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
  };

  private static CompletionResponse CompletionWithThinkingBlocks((string Sig, string Text)[] blocks)
  {
    var arr = new JsonArray();
    foreach (var (sig, text) in blocks)
    {
      arr.Add(new JsonObject { ["signature"] = sig, ["thinking"] = text });
    }

    return new CompletionResponse
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
            ThinkingBlocks = arr,
            ToolCalls = new()
            {
              new ToolCallPayload
              {
                Id = "call-1",
                Type = "function",
                Function = new FunctionCallPayload { Name = "submit_final", Arguments = """{"done": true}""" }
              }
            }
          }
        }
      },
      Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
    };
  }

  private static CompletionResponse SubmitFinalOnly() => new()
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
              Function = new FunctionCallPayload { Name = "submit_final", Arguments = """{"done": true}""" }
            }
          }
        }
      }
    },
    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
  };

  private sealed class FakeLlmClient : ILlmClient
  {
    private readonly CompletionResponse _response;
    public ConcurrentBag<CompletionRequest> Requests { get; } = new();

    public FakeLlmClient(CompletionResponse response) => _response = response;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      Requests.Add(request);
      return Task.FromResult(_response);
    }
  }

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

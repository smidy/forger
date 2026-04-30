using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Agent.Compaction;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the <c>summarise</c> and <c>window</c> compaction
/// strategies. Exercises the strategy pipeline at the <c>CompactResult</c>
/// level — building a 20 × ~10 KB tool-result messages list, running the
/// strategy, and asserting compaction metrics.
/// </summary>
public class ContextCompactionStrategiesE2ETests
{
  [Fact]
  public async Task Summarise_strategy_reduces_messages_below_half()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // Build 20 iterations, each with ~10 KB tool result content
    var messages = BuildTypicalMessages(iterationCount: 20, toolContentLength: 10_000);

    // Script the summariser to return a short summary
    var summariser = new ScriptedSummariser("The agent explored the codebase, identified the relevant files, and made targeted edits to implement the feature. All tests pass.");
    var cfg = new AgentCompactionConfig
    {
      KeepRecentIterations = 3,
      SummarisationModel = "test-summariser"
    };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 20, TestContext.Current.CancellationToken);

    // Should have compacted some iterations
    result.CompactedIterations.Should().NotBeEmpty();
    result.MessagesBefore.Should().Be(messages.Count);

    // Core acceptance: MessagesAfter < MessagesBefore / 2
    // Original: 1 system + 1 user + 20 assistant + 20 tool = 42 messages
    // Expected: 1 system + 1 user + synthetic pair(2) + 3 recent group(6) = 10
    // 42 / 2 = 21 > 10 ✓
    result.Messages.Count.Should().BeLessThan(result.MessagesBefore / 2);

    // Estimated tokens should also have reduced
    result.EstimatedTokensAfter.Should().BeLessThan(result.EstimatedTokensBefore);

    // Should have archive directory
    result.ArchivePath.Should().NotBeNull();
    Directory.Exists(result.ArchivePath).Should().BeTrue();

    // Verify synthetic pair structure
    var synAsst = result.Messages.FirstOrDefault(m =>
    {
      var content = m?["content"]?.GetValue<string>() ?? "";
      return content.StartsWith("[compacted]");
    });
    synAsst.Should().NotBeNull();
    synAsst!["tool_calls"].Should().NotBeNull();
    synAsst["tool_calls"]!.AsArray().Should().NotBeEmpty();

    var synTool = result.Messages.FirstOrDefault(m =>
    {
      var callId = m?["tool_call_id"]?.GetValue<string>() ?? "";
      return callId.StartsWith("compaction_");
    });
    synTool.Should().NotBeNull();
    var toolContent = synTool!["content"]?.GetValue<string>() ?? "";
    toolContent.Should().Contain("_compacted");
  }

  [Fact]
  public async Task Summarise_strategy_trace_event_has_correct_counts()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var messages = BuildTypicalMessages(iterationCount: 10, toolContentLength: 5000);
    var summariser = new ScriptedSummariser("A short summary.");
    var cfg = new AgentCompactionConfig
    {
      KeepRecentIterations = 2,
      SummarisationModel = "test-summariser"
    };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 10, TestContext.Current.CancellationToken);

    result.CompactedIterations.Should().NotBeEmpty();

    // Build a trace event from the result (same way ContextCompactor does)
    var traceEvent = new ContextCompactedEvent
    {
      Iteration = 10,
      Strategy = "summarise",
      MessagesBefore = result.MessagesBefore,
      MessagesAfter = result.Messages.Count,
      EstimatedTokensBefore = result.EstimatedTokensBefore,
      EstimatedTokensAfter = result.EstimatedTokensAfter,
      CompactedIterations = result.CompactedIterations,
      ArchivePath = result.ArchivePath,
      SummariserModel = cfg.SummarisationModel,
      SummariserTokens = new SummariserTokensPayload
      {
        Prompt = 100,
        Completion = 20
      }
    };

    traceEvent.Kind.Should().Be("context_compacted");
    traceEvent.Strategy.Should().Be("summarise");
    traceEvent.SummariserModel.Should().Be("test-summariser");
    traceEvent.SummariserTokens.Should().NotBeNull();
    traceEvent.SummariserTokens!.Prompt.Should().Be(100);
    traceEvent.SummariserTokens.Completion.Should().Be(20);
    traceEvent.MessagesAfter.Should().BeLessThan(traceEvent.MessagesBefore);
  }

  [Fact]
  public async Task Window_strategy_drops_oldest_iterations()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // 20 iterations, keepRecent=3 => 17 candidates
    var messages = BuildTypicalMessages(iterationCount: 20, toolContentLength: 500);
    var cfg = new AgentCompactionConfig
    {
      KeepRecentIterations = 3,
      DropOldestIterations = 14  // drop 14 of 17 candidates, keep 3 middle + 3 recent
    };
    var ctx = BuildToolContext(tmp.Path, stageDir);

    var result = await WindowStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 20, TestContext.Current.CancellationToken);

    // Should have dropped exactly 14 groups
    result.CompactedIterations.Should().HaveCount(14);
    result.MessagesBefore.Should().Be(messages.Count);

    // Expected: system(1) + user(1) + 3 kept groups(6) + 3 recent groups(6) = 14
    result.Messages.Should().HaveCount(14);
  }

  // ─── Helpers ──────────────────────────────────────────────────────────────

  private static List<JsonNode> BuildTypicalMessages(int iterationCount, int toolContentLength = 100)
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "System prompt here" },
      new JsonObject { ["role"] = "user", ["content"] = "User prompt here" }
    };

    for (var i = 0; i < iterationCount; i++)
    {
      messages.Add(new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = $"Thinking step {i}",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject
          {
            ["id"] = $"call-{i}",
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" }
          }
        }
      });
      messages.Add(new JsonObject
      {
        ["role"] = "tool",
        ["tool_call_id"] = $"call-{i}",
        ["content"] = new string('x', toolContentLength)
      });
    }

    return messages;
  }

  private static ToolContext BuildToolContext(string runRoot, string stageDir)
  {
    var idx = 0;
    var trace = new CapturingTraceSink();
    return new ToolContext(
      RunId: "test-run",
      RunWorkspace: runRoot,
      StageDir: stageDir,
      StageId: "agent",
      IterationIndex: null,
      Llm: new StubLlmClient(),
      Trace: trace,
      Logger: NullLogger.Instance,
      CancellationToken: CancellationToken.None,
      NextToolOutputIdx: () => ++idx);
  }

  private static ToolContext BuildToolContext(string runRoot, string stageDir, ILlmClient llm)
  {
    var idx = 0;
    var trace = new CapturingTraceSink();
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

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class ScriptedSummariser : ILlmClient
  {
    private readonly string _summary;

    public ScriptedSummariser(string summary)
    {
      _summary = summary;
    }

    public string? LastUsedModel { get; private set; }

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      LastUsedModel = request.Model;

      return Task.FromResult(new CompletionResponse
      {
        Id = "test-summary",
        Choices = new List<CompletionChoice>
        {
          new()
          {
            Index = 0,
            FinishReason = "stop",
            Message = new ChatMessagePayload
            {
              Role = "assistant",
              Content = JsonValue.Create(_summary)
            }
          }
        },
        Usage = new UsagePayload
        {
          PromptTokens = 100,
          CompletionTokens = 20
        }
      });
    }
  }

  private sealed class StubLlmClient : ILlmClient
  {
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("Should not be called in window strategy tests");
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
